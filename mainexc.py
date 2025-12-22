# media_sync.py
# Python 3.7+ (tested)
# Dependencies: requests, pyserial, pywin32
# pip install requests pyserial pywin32

#from opencc import OpenCC
#import traceback
import re
import time
import threading
import requests
import serial
import sys
from difflib import SequenceMatcher

# If on Windows and using named pipe client:
try:
    import win32file, pywintypes
    PIPE_AVAILABLE = True
except Exception:
    PIPE_AVAILABLE = False

# -----------------------
# Configuration
# -----------------------
PIPE_MODE = True            # True: read from named pipe; False: read from stdin
PIPE_NAME = r'\\.\pipe\MusicInfoPipe'  # full pipe path for pywin32 CreateFile
SERIAL_PORT = 'COM27'        # 修改为你的串口
SERIAL_BAUD = 115200

# 自动轮询播放位置并发送歌词的间隔（秒） #fix13 part1
POSITION_WATCH_INTERVAL = 0.5  # 可调为 0.2 ~ 1.0，根据需要

# 控制 watcher 线程的停止标志          #fix13 part2
_position_watcher_stop = False
_position_watcher_lock = threading.Lock()

NETEASE_SEARCH_LIMIT = 5
DURATION_TOLERANCE_SEC = 8.0   # 时长匹配容差（秒）
HTTP_TIMEOUT = 6.0

# 控制发送行为
SEND_ON_MATCH = True
MIN_SEND_INTERVAL = 0.05   # 向串口发送最小间隔，防止刷屏（秒）

# 播放时间锚点（线程安全访问请用 anchor_lock）  #fix 15 part 1
anchor_lock = threading.Lock()
# 当 anchor_ts 不为 None 时，表示从 anchor_ts 开始播放，anchor_pos 为该时刻的歌曲位置（秒）
playback_anchor_ts = None    # wall-clock 时间戳（秒）
playback_anchor_pos = 0.0    # 对应的歌曲位置（秒）
# 暂停标志（可选）
is_paused_by_anchor = False

# 配置：轮询间隔与最小位置变化阈值（秒） #fix15 part 3 layer 1
POSITION_WATCH_INTERVAL = 0.20 
POSITION_CHANGE_THRESHOLD = 0.05

_re_hiragana = re.compile(r'[\u3040-\u309F]')
_re_katakana = re.compile(r'[\u30A0-\u30FF]')
_re_hangul = re.compile(r'[\uAC00-\uD7AF]')
_re_cjk = re.compile(r'[\u4E00-\u9FFF\u3400-\u4DBF\uF900-\uFAFF]')
_re_latin = re.compile(r'[A-Za-z]')

# -----------------------
# 全局状态（线程安全）
# -----------------------
_state_lock = threading.Lock()
state = {
    "source": None,
    "title": None,
    "artist": None,
    "album": None,
    "playback": None,   # "Playing" / "Paused" / ...
    "position": None,   # seconds (float)
    "duration": None,   # seconds (float)
    "last_line": None,  # 原始最后一行
    "last_update_ts": None
}

# 歌词缓存与当前歌曲信息
lyrics_lock = threading.Lock()
current_song = {
    "song_id": None,
    "lyrics": [],   # list of (time_seconds, text)
    "fetched_at": None
}

# 串口发送控制
serial_lock = threading.Lock()
ser = None
last_sent_ts = 0.0
last_sent_lyric_time = None

# -----------------------
# 工具函数
# -----------------------

#safe update #fix9 part1    #fix10 part1 #fix11 part1
def safe_update_state(**kwargs):
    #仅在传入值不为 None 且非空字符串时更新 state,避免意外覆盖已有字段为 None
    with _state_lock:
        for k, v in kwargs.items():
            if k in state:
                if v is None:
                    continue
                if isinstance(v, str) and v.strip() == "":
                    continue
                state[k] = v
        state['last_update_ts'] = time.time()

#歌词自动输出       #fix14 part3    #fix15 part4
def position_watcher(interval=POSITION_WATCH_INTERVAL):
    global _position_watcher_stop
    last_pos = None
    print("position_watcher started, interval=", interval)
    try:
        while True:
            with _position_watcher_lock:
                if _position_watcher_stop:
                    break
            st = get_state_copy()
            playback = st.get('playback')
            # 只在播放时自动推进
            if playback and str(playback).lower() == 'playing':
                pos = compute_current_position_from_anchor()
                # 只有当 position 明显变化时才调用，避免无谓调用
                if last_pos is None or abs(pos - last_pos) >= POSITION_CHANGE_THRESHOLD:
                    # 把计算出的 pos 写回 state（可选），以便其它逻辑使用
                    safe_update_state(position=pos)
                    last_pos = pos
                    try:
                        compute_and_send_current_lyric()
                    except Exception as e:
                        print("position_watcher compute error:", e)
            else:
                last_pos = None
            time.sleep(interval)
    except Exception as e:
        print("position_watcher fatal error:", e)

def get_state_copy():
    with _state_lock:
        return dict(state)

def similar(a, b):
    if not a or not b:
        return 0.0
    return SequenceMatcher(None, a.lower(), b.lower()).ratio()

def parse_time_to_seconds(tstr):
    # 支持 hh:mm:ss(.ms) 或 mm:ss(.ms)
    if not tstr:
        return None
    try:
        parts = tstr.split(':')
        if len(parts) == 3:
            h = int(parts[0])
            m = int(parts[1])
            s = float(parts[2])
            return h*3600 + m*60 + s
        elif len(parts) == 2:
            m = int(parts[0])
            s = float(parts[1])
            return m*60 + s
    except Exception:
        return None
    return None

# 解析 LRC 文本为 (seconds, text) 列表
_re_lrc_time = re.compile(r'\[(\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?)\]')
def parse_lrc(lrc_text):
    lines = []
    if not lrc_text:
        return lines
    for raw in lrc_text.splitlines():
        raw = raw.strip()
        if not raw:
            continue
        times = _re_lrc_time.findall(raw)
        text = _re_lrc_time.sub('', raw).strip()
        for t in times:
            sec = parse_time_to_seconds(t)
            if sec is not None:
                lines.append((sec, text))
    # sort by time
    lines.sort(key=lambda x: x[0])
    return lines

#创建lrc歌词组 双语 #fix5 part1 #fix6 part1
def build_grouped_lyrics(lrc_text, tlyric_text=None):
    #返回 [(time_seconds, [(source, text), ...]), ...]
    #source 为 'lrc' 或 'tlyric'，保留重复文本并保持加入顺序。
    parsed_main = parse_lrc(lrc_text) if lrc_text else []
    parsed_trans = parse_lrc(tlyric_text) if tlyric_text else []
    grouped = {}   # key -> (orig_time, [(source, text), ...])
    order = []

    def key_of(t):
        return round(t, 3)  # 毫秒对齐

    def add_entry(t, txt, src):
        if txt is None:
            return
        txt = txt.strip()
        if not txt:
            return
        k = key_of(t)
        if k not in grouped:
            grouped[k] = [t, []]
            order.append(k)
        # 不去重：直接追加 (source, text)
        grouped[k][1].append((src, txt))

    # 先主歌词（lrc）
    for t, txt in parsed_main:
        add_entry(t, txt, 'lrc')
    # 再译文（tlyric）
    for t, txt in parsed_trans:
        add_entry(t, txt, 'tlyric')

    ordered_keys = sorted(order)
    result = []
    for k in ordered_keys:
        time_float = grouped[k][0]
        entries = grouped[k][1]  # list of (source, text)
        result.append((time_float, entries))
    return result

#检测当前时间 #fix15 part2
def set_playback_anchor(pos_seconds, at_ts=None):
    #将播放锚点设置为：在 wall-clock 时间 at_ts 时，歌曲位置为 pos_seconds。
    #如果 at_ts 为 None，使用当前时间.
    global playback_anchor_ts, playback_anchor_pos, is_paused_by_anchor
    if at_ts is None:
        at_ts = time.time()
    with anchor_lock:
        playback_anchor_ts = float(at_ts)
        playback_anchor_pos = float(pos_seconds or 0.0)
        is_paused_by_anchor = False
    # debug
    print("[ANCHOR] set anchor pos=%.3f at_ts=%.3f" % (playback_anchor_pos, playback_anchor_ts))

def clear_playback_anchor():
    #将锚点清空（例如进入暂停时）
    global playback_anchor_ts, playback_anchor_pos, is_paused_by_anchor
    with anchor_lock:
        # 保留当前位置到 anchor_pos，清空 anchor_ts 表示暂停状态
        if playback_anchor_ts is not None:
            # 计算当前位置并保存为 anchor_pos
            now = time.time()
            playback_anchor_pos = playback_anchor_pos + max(0.0, now - playback_anchor_ts)
        playback_anchor_ts = None
        is_paused_by_anchor = True
    print("[ANCHOR] cleared anchor, paused at pos=%.3f" % (playback_anchor_pos))

def compute_current_position_from_anchor():
    #返回基于锚点计算的当前歌曲位置（秒）。
    #逻辑：
    #  - 如果 anchor_ts 不为 None：返回 anchor_pos + (now - anchor_ts)
    #  - 否则返回 anchor_pos（表示暂停时保存的位置）
    with anchor_lock:
        if playback_anchor_ts is None:
            return float(playback_anchor_pos or 0.0)
        else:
            return float(playback_anchor_pos + max(0.0, time.time() - playback_anchor_ts))

# 选择最接近的歌词行索引（返回索引或 None）
def find_lyric_index_for_time(lyrics, target_sec):
    if not lyrics:
        return None
    # 找到最后一个 time <= target_sec
    idx = None
    for i, (t, txt) in enumerate(lyrics):
        if t <= target_sec + 0.0001:
            idx = i
        else:
            break
    if idx is None:
        # 如果 target 在第一行之前，返回 0
        return 0
    return idx

# -----------------------
# NetEase API 调用
# -----------------------
NETEASE_HEADERS = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
    'Referer': 'https://music.163.com',
    'Accept': 'application/json, text/plain, */*'
}

def netease_search(query, limit=NETEASE_SEARCH_LIMIT):
    # query: string
    url = 'https://music.163.com/api/search/get'
    params = {'s': query, 'type': 1, 'offset': 0, 'limit': limit}
    try:
        r = requests.get(url, params=params, headers=NETEASE_HEADERS, timeout=HTTP_TIMEOUT)
        r.raise_for_status()
        return r.json()
    except Exception as e:
        print("netease_search error:", e)
        return None

def netease_get_lyric(song_id):
    url = 'https://music.163.com/api/song/lyric'
    params = {'os': 'pc', 'id': song_id, 'lv': -1, 'tv': -1}
    try:
        r = requests.get(url, params=params, headers=NETEASE_HEADERS, timeout=HTTP_TIMEOUT)
        r.raise_for_status()
        return r.json()
    except Exception as e:
        print("netease_get_lyric error:", e)
        return None

# 选择最合适的 song id（基于 title/artist/album/duration）
def choose_best_song_id(title, artist, album, duration_sec, search_limit=NETEASE_SEARCH_LIMIT):
    # 构造关键词：title + artist + album（若有）
    keywords = ' '.join([x for x in [title, artist, album] if x])
    if not keywords:
        return None
    data = netease_search(keywords, limit=search_limit)
    if not data or 'result' not in data or 'songs' not in data['result']:
        return None
    candidates = data['result']['songs']
    best = None
    best_score = -1.0
    for c in candidates:
        sid = c.get('id')
        t = c.get('name') or ''
        ar_list = c.get('artists') or []
        ar = ','.join([a.get('name','') for a in ar_list])
        al = c.get('album', {}).get('name', '')
        # duration from API in ms
        dur_ms = c.get('duration') or c.get('dt') or None
        dur_sec = None
        if dur_ms:
            try:
                dur_sec = float(dur_ms) / 1000.0
            except:
                dur_sec = None
        # compute similarity score
        s_title = similar(title, t)
        s_artist = similar(artist, ar)
        s_album = similar(album, al)
        # duration score: if duration available, penalize difference
        dur_score = 1.0
        if duration_sec and dur_sec:
            diff = abs(duration_sec - dur_sec)
            if diff <= DURATION_TOLERANCE_SEC:
                dur_score = 1.0
            else:
                # decay
                dur_score = max(0.0, 1.0 - (diff / max(duration_sec, dur_sec, 1.0)))
        # weighted sum
        score = (0.6 * s_title) + (0.25 * s_artist) + (0.1 * s_album)
        score *= dur_score
        # small boost if exact artist substring
        if artist and artist.lower() in ar.lower():
            score += 0.05
        if score > best_score:
            best_score = score
            best = (sid, t, ar, al, dur_sec, score)
    if best:
        return best[0]
    return None

# -----------------------
# 串口发送
# -----------------------
def open_serial(port, baud):
    global ser
    try:
        s = serial.Serial(port, baud, timeout=0.5)
        print("Serial opened:", port, baud)
        ser = s
    except Exception as e:
        print("Failed to open serial:", e)
        ser = None

#send to serial lyric line 1 and 2
# 依赖：全局变量 ser, serial_lock, MIN_SEND_INTERVAL, last_sent_ts 已在脚本中定义
# 这两个函数分别用于发送第一行和第二行文本，线程安全并带节流与可选强制发送参数。
def send_to_serial_line1(text: str, force: bool = False) -> None:
    #逐行发送第一条文本（LRC1 前缀由调用方添加或在上层处理）。
    #参数:text  - 要发送的纯文本（不包含额外换行，函数会自动添加换行）  force - True 时忽略最小发送间隔立即发送
    global last_sent_ts, ser
    if not SEND_ON_MATCH:
        return
    if ser is None:
        # 串口未打开，直接返回（或可在此处记录日志）
        return
    now = time.time()
    if not force and (now - last_sent_ts) < MIN_SEND_INTERVAL:
        return
    try:
        with serial_lock:
            payload = text + '\n\r'
            ser.write(b'\x0C')
            ser.write(payload.encode(simple_detect_line_language(payload,1), errors='ignore'))
            ser.flush()
        last_sent_ts = now
    except Exception as e:
        print("Serial send (line1) error:", e)

def send_to_serial_line2(text: str, force: bool = False) -> None:
    #逐行发送第二条文本(LRC2 前缀由调用方添加或在上层处理)。
    #参数与 send_to_serial_line1 相同。
    global last_sent_ts, ser
    if not SEND_ON_MATCH:
        return
    if ser is None:
        return
    now = time.time()
    if not force and (now - last_sent_ts) < MIN_SEND_INTERVAL:
        return
    try:
        with serial_lock:
            #payload = text
            ser.write(text.encode(simple_detect_line_language(text,2), errors='ignore'))
            ser.flush()
        last_sent_ts = now
    except Exception as e:
        print("Serial send (line2) error:", e)

# -----------------------
# 处理逻辑：当收到新管道行时解析并触发动作
# -----------------------
_re_timeline = re.compile(r"""^\[.*?\]\s*(?P<src>\S+)\s+timeline\s+is\s+now\s+(?P<pos>\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?)/(?P<dur>\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?)""", re.IGNORECASE | re.VERBOSE)
_re_playstate = re.compile(r"""^\[.*?\]\s*(?P<src>\S+)\s+is\s+now\s+(?P<state>Playing|Paused|Stopped|Buffering|Closed)""", re.IGNORECASE | re.VERBOSE)
_re_playing_info = re.compile(r"""^\[.*?\]\s*(?P<src>\S+)\s+is\s+now\s+playing\s+(?P<title>.+?)\s+by\s+(?P<artist>.+)""", re.IGNORECASE | re.VERBOSE)

# 解析行并更新 state  #fix in 6,7,12,13,14
def parse_pipe_line(line):
    line = line.strip()
    if not line:
        return

    # 先统一更新 last_line 字段（不覆盖其它字段）
    safe_update_state(last_line=line)
    lower = line.lower()

    # 明确的暂停行（例如 "is now Paused"）
    if 'is now paused' in lower:
        # 更新播放状态为 Paused
        safe_update_state(playback='Paused')
        on_playback_state_change('Paused')
        return

    # 明确的 playing 信息（带 "is now playing" 且包含 " by " 表示带有 title+artist -> 切歌）
    if 'is now playing' in lower and ' by ' in lower:
        # 尝试用正则提取 title 和 artist（回退到简单切分）
        m = _re_playing_info.match(line)
        if m:
            src = m.group('src')
            title = m.group('title').strip()
            artist = m.group('artist').strip()
            safe_update_state(source=src, title=title, artist=artist)
        else:
            # 回退解析：取 "is now playing" 之后，按 " by " 分割
            try:
                idx = lower.index('is now playing')
                tail = line[idx + len('is now playing'):].strip()
                if ' by ' in tail.lower():
                    parts = re.split(r'\s+by\s+', tail, flags=re.IGNORECASE)
                    title = parts[0].strip()
                    artist = parts[1].strip() if len(parts) > 1 else None
                    safe_update_state(title=title, artist=artist)
            except Exception:
                pass
        # 切歌：触发新歌检测（不依赖 playback 变化）
        on_new_song_detected()
        return

    # 只有 "is now playing"（但没有 "by"） -> 视为从暂停/停止开始播放（metadata 可能稍后到）
    if 'is now playing' in lower:
        # 标记为播放状态；若后续有 title 到达会触发 on_new_song_detected
        safe_update_state(playback='Playing')
        on_playback_state_change('Playing')
        # 如果当前 state 已有 title 且与缓存不同，触发搜索
        st = get_state_copy()
        title_now = st.get('title')
        if title_now:
            with lyrics_lock:
                cached_title = current_song.get('title_cached')
                has_lyrics = bool(current_song.get('lyrics'))
            if cached_title != title_now or not has_lyrics:
                with lyrics_lock:
                    current_song['title_cached'] = title_now
                    current_song['fetch_start'] = time.time()
                    current_song['fetch_duration'] = None
                    current_song['song_id'] = None
                    current_song['lyrics'] = []
                    current_song['fetched_at'] = None
                #这里的作用未知，但是注释掉后不会搜索两遍歌词，但是第一次运行程序有可能印不出来歌词，，
                t = threading.Thread(target=search_and_fetch_lyrics, args=(title_now, st.get('artist'), st.get('album'), st.get('duration')), daemon=True)
                t.start()
        return

    # timeline 行（保持原有行为）
    m = _re_timeline.match(line)
    if m:
        src = m.group('src')
        pos = parse_time_to_seconds(m.group('pos'))
        dur = parse_time_to_seconds(m.group('dur'))
        safe_update_state(source=src, position=pos, duration=dur)
        # timeline 分支解析后  #fix15 part3
        safe_update_state(source=src, position=pos, duration=dur, last_line=line)
        # 更新锚点，防止暂停时继续推进
        set_playback_anchor(pos, at_ts=time.time())
        on_timeline_update()
        return

    # 其它带 "is now" 的播放状态（例如 Stopped/Buffering 等），按原逻辑处理
    m = _re_playstate.match(line)
    if m:
        src = m.group('src')
        st = m.group('state').capitalize()
        safe_update_state(source=src, playback=st)
        on_playback_state_change(st)
        return

    # fallback: 如果行包含 "is now playing" 但解析失败，尝试保留尾部为 title
    if 'is now playing' in lower:
        try:
            idx = lower.index('is now playing')
            tail = line[idx + len('is now playing'):].strip()
            if tail:
                safe_update_state(title=tail)
                on_new_song_detected()
        except Exception:
            pass

# 当检测到新歌曲信息时（title/artist/album 更新）    #change 1st here fix
def on_new_song_detected():
    set_playback_anchor(0.0, at_ts=time.time())        #own fix16 1
    st = get_state_copy()
    title = st.get('title')
    artist = st.get('artist')
    album = st.get('album')
    duration = st.get('duration')
    if not title:
        return
    # 记录搜索开始时间（线程安全）
    with lyrics_lock:
        current_song['fetch_start'] = time.time()
        current_song['fetch_duration'] = None
        current_song['song_id'] = None
        current_song['lyrics'] = []
        current_song['fetched_at'] = None

    t = threading.Thread(target=search_and_fetch_lyrics, args=(title, artist, album, duration), daemon=True)
    t.start()

# 当播放状态变化（暂停/播放）
def on_playback_state_change(state_str):
    print("Playback state:", state_str)
    # if paused, we should stop sending; if playing, resume sending (handled in send logic)

# 当 timeline 更新（position/duration）
def on_timeline_update():
    # reposition lyric send based on new position
    st = get_state_copy()
    if st.get('playback') and st.get('playback').lower() == 'paused':
        return
    # compute send target and send nearest lyric
    t = threading.Thread(target=compute_and_send_current_lyric, daemon=True)
    t.start()

# 搜索并获取歌词（会更新 current_song）         #fix 1 part 2
def search_and_fetch_lyrics(title, artist, album, duration):
    try:
        print("Searching for:", title, artist, album, duration)
        # 记录请求开始（也可在 choose_best_song_id 内记录更细粒度 RTT）
        search_start = time.time()
        sid = choose_best_song_id(title, artist, album, duration)
        print("Searched SongId is "+str(sid))
        if not sid:
            print("No song id found for", title, artist)
            return

        # 获取歌词并测量请求耗时
        #lyric_start = time.time()
        #data, http_rtt = netease_get_lyric(sid)  # netease_get_lyric 返回 (json, rtt)
        res = netease_get_lyric(sid)                #fix 2 part 1
        if isinstance(res, tuple) and len(res) == 2:
            data
        else:
            data = res
        #lyric_end = time.time()

        if not data:
            print("No lyric data")
            return

        # 写入 current_song 之后，确保有锚点（如果当前处于播放）
        st = get_state_copy()
        if st.get('playback') and str(st.get('playback')).lower() == 'playing':
        # 如果 state 中有 position，使用它；否则把 anchor 设为 0 起点（或不设）
            pos = st.get('position')
            if pos is not None:
                set_playback_anchor(pos, at_ts=time.time())
            else:
        # 没有 position 时也可以把 anchor 设为 now（从 0 开始计时），或选择不设
                set_playback_anchor(0.0, at_ts=time.time())

        # 解析 lrc_text
        # 从 data 中提取主歌词与译文   #fix5 part2
        lrc_text = None
        tlyric_text = None
        if 'lrc' in data and data['lrc'] and 'lyric' in data['lrc']:
          lrc_text = data['lrc']['lyric']
        if 'tlyric' in data and data['tlyric'] and 'lyric' in data['tlyric']:
            tlyric_text = data['tlyric']['lyric']

        # 如果只有 tlyric 可用，也当作主歌词处理
        if not lrc_text and tlyric_text:
            lrc_text = tlyric_text
            tlyric_text = None

        if not lrc_text:
            print("No lrc text in response for id:", sid)
            return

        # 使用合并函数，得到 grouped 结构
        grouped = build_grouped_lyrics(lrc_text, tlyric_text)

        # 记录 fetch_end 与 fetch_duration（从 fetch_start 到现在的总耗时）
        fetch_end = time.time()
        with lyrics_lock:
            fetch_start = current_song.get('fetch_start') or search_start
            fetch_duration = fetch_end - fetch_start
            current_song['song_id'] = sid
            # 存储 grouped 结构（每项为 (time_seconds, [texts])）
            current_song['lyrics'] = grouped
            current_song['fetched_at'] = fetch_end
            current_song['fetch_duration'] = fetch_duration

        print("Fetched grouped lyrics entries:", len(grouped))
        for i, (t, texts) in enumerate(grouped[:8]):
            print("  [%02d] time=%.3f lines=%d -> %s" % (i, t, len(texts), texts))
    except Exception as e: 
        print("search_and_fetch_lyrics error:", e) 

# 计算目标时间并发送最接近的歌词行          #fix 1 part 3 #fix4 part1
# 假设 send_to_serial_line1 和 send_to_serial_line2 已存在（如前所示）
# 并且全局变量 last_sent_lyric_time 已定义
def compute_and_send_current_lyric():
    global last_sent_lyric_time
    st = get_state_copy()
    if not st.get('title'):
        return
    if st.get('playback') and st.get('playback').lower() == 'paused':
        return
    pos = st.get('position')
    if pos is None:
        return

    with lyrics_lock:
        grouped = list(current_song.get('lyrics') or [])
        fetched_at = current_song.get('fetched_at')

    #if fetch_duration is not None:
        lyric_offset = 0.15

    target_time = pos

    # 找到最后一个 time <= target_time
    idx = None
    for i, (t, entries) in enumerate(grouped):
        if t <= target_time + 1e-6:
            idx = i
        else:
            break
    if idx is None:
        return

    base_time, entries = grouped[idx]
    if not entries:
        return

    # 去重发送判断（按时间）
    if last_sent_lyric_time is not None and abs(last_sent_lyric_time - base_time) < 1e-6:
        return

    # entries 是 [(source, text), ...]，按顺序取前两条文本
    if len(entries) == 1:
        text1 = entries[0][1]
        text2 = ""   # 清空第二行
    else:
        text1 = entries[0][1]
        text2 = entries[1][1]

    #line1 = text1  #line1 = "LRC1:=" + text1    #line2 = text2    #line2 = "LRC2:=" + text2
    # 逐行发送，使用 force=True 确保连续发送
    send_to_serial_line1(text1, force=True)
    send_to_serial_line2(text2, force=True)

    last_sent_lyric_time = base_time
    print("Sent grouped lyric time=%.3f entries=%d pos=%.3f lyric_offset=%.3f" %
          (base_time, len(entries), pos, lyric_offset))

# -----------------------
# 管道读取线程（Windows named pipe via pywin32）
# -----------------------
def pipe_reader_loop(pipe_name):
    print("Pipe reader started:", pipe_name)
    while True:
        try:
            # CreateFile to open existing pipe
            handle = win32file.CreateFile(
                pipe_name,
                win32file.GENERIC_READ,0, None,
                win32file.OPEN_EXISTING,0, None
            )
            # read loop
            data = b''
            while True:
                try:
                    hr, chunk = win32file.ReadFile(handle, 4096)
                    if not chunk:
                        break
                    data += chunk
                    # split lines
                    while b'\n' in data:
                        line, data = data.split(b'\n', 1)
                        try:
                            s = line.decode('utf-8', errors='ignore').strip()
                            if s:
                                print("[PIPE] " + s)
                                parse_pipe_line(s)
                        except Exception as e:
                            print("line decode error", e)
                except pywintypes.error as e:
                    # pipe closed or error
                    break
            try:
                win32file.CloseHandle(handle)
            except:
                pass
        except pywintypes.error as e:
            # no server yet or other error; wait and retry
            time.sleep(0.5)
        except Exception as e:
            print("pipe_reader_loop error:", e)
            time.sleep(0.5)

# stdin reader fallback
def stdin_reader_loop():
    print("stdin reader started")
    for raw in sys.stdin:
        s = raw.strip()
        if s:
            print("[STDIN] " + s)
            parse_pipe_line(s)

# -----------------------
# 主入口
# -----------------------
def main():     #fix14 part4
    global ser, _position_watcher_stop
    # open serial
    try:
        open_serial(SERIAL_PORT, SERIAL_BAUD)
    #initalize screen
        ser.write(b'\x0C\x1f\x03')
        ser.write(b'\x1F\x58\x03')      #set brightness 03-75%
    except Exception as e:
        print("Serial open failed:", e)

    # start pipe or stdin reader
    if PIPE_MODE and PIPE_AVAILABLE:
        t = threading.Thread(target=pipe_reader_loop, args=(PIPE_NAME,), daemon=True)
        t.start()
    else:
        print("PIPE_MODE disabled or pywin32 not available; using stdin")
        t = threading.Thread(target=stdin_reader_loop, daemon=True)
        t.start()

    # start position watcher thread
    watcher_thread = threading.Thread(target=position_watcher, daemon=True)
    watcher_thread.start()

    # main loop: keep alive and optionally print state
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        print("exiting...")
        # stop watcher
        with _position_watcher_lock:
            _position_watcher_stop = True
        # give watcher a moment to exit
        watcher_thread.join(timeout=1.0)
        try:
            if ser:
                ser.close()
        except:
            pass

# 简单的繁体字样本集，用于快速判断（可按需扩充）
_TRADITIONAL_SAMPLE = set(list("體愛萬與麼裏後麼廣電學氣風顏龍麵麥"))

#text language test return gb2312 big5 shihftjis ksc5601 or ascii
def simple_detect_line_language(text,Line):
    if not text or not text.strip():
        return 'ascii'

    counts = {'hiragana':0,'katakana':0,'hangul':0,'cjk':0,'latin':0,'other':0}
    for ch in text:
        if _re_hiragana.search(ch):
            counts['hiragana'] += 1
        elif _re_katakana.search(ch):
            counts['katakana'] += 1
        elif _re_hangul.search(ch):
            counts['hangul'] += 1
        elif _re_cjk.search(ch):
            counts['cjk'] += 1
        elif _re_latin.search(ch):
            counts['latin'] += 1
        else:
            counts['other'] += 1

    total = sum(counts.values())
    if total == 0:
        ser.write(b'\x1F\x28\x67\x02\x00')
        return 'ascii'

    # 优先判断日文
    if counts['hiragana'] + counts['katakana'] > 0: # and (counts['hiragana'] + counts['katakana']) >= max(counts['cjk'], counts['hangul']):
        ser.write(b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x00')
        print("L is JP")
        return 'shift_jis'
    # 韩文
    if counts['hangul'] > 0 and counts['hangul'] >= counts['cjk']:
        ser.write(b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x01')
        print("L is KR")
        return 'KSC5601'
    # 中文（汉字占主导）
    if counts['cjk'] > 0 and counts['cjk'] >= max(counts['hangul'], counts['hiragana'] + counts['katakana']):
        # 简单繁体判定：若句中出现样本繁体字则判为繁体，否则判为简体
        for ch in text:
            if ch in _TRADITIONAL_SAMPLE:
                ser.write(b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x03')
                print("Lis zhT")
                return 'Big5'
        # 若句子很短且混合其他脚本，返回 mixed
            if total <= 6 and (counts['latin'] > 0 or counts['other'] > 0):
                if Line==1:
                    ser.write(b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x00')
                    print("L1 is JP")
                    return 'Shift_JIS'
            ser.write(b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x02')
            print("L is UnK may zhS")
            return 'GB2312'
        ser.write(b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x02')
        print("here?")
        return 'GB2312'
    # 英文为主
    if counts['latin'] > 0 and counts['latin'] >= max(counts['cjk'], counts['hangul'], counts['hiragana'] + counts['katakana']):
        ser.write(b'\x1F\x28\x67\x02\x00')
        print("L"+str(Line)+" is EN")
        return 'ascii'
    # 混合或无法判定
    print("L is UnK2")
    ser.write(b'\x1F\x28\x67\x02\x00')
    return 'ascii'

if __name__ == '__main__':
    main()
