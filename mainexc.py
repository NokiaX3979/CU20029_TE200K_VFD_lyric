# media_sync.py
# Python 3.7+ (tested)
# Dependencies: requests, pyserial, pywin32, zhconv
# pip install requests pyserial pywin32 zhconv

import os
import re
import time
import threading
import requests
import serial
import serial.tools.list_ports
import sys
import zhconv
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
PIPE_MODE = True  # True: read from named pipe; False: read from stdin
PIPE_NAME = r'\\.\pipe\MusicInfoPipe'
SERIAL_BAUD = 38400
# 自动轮询播放位置并发送歌词的间隔（秒）
POSITION_WATCH_INTERVAL = 0.15       # 可调为 0.2 ~ 1.0，根据需要
POSITION_CHANGE_THRESHOLD = 0.05    # 最小位置变化阈值
LYRIC_OFFSET = 0.0                 # 全局统一歌词偏移量（秒）：正数提前，负数延后

NETEASE_SEARCH_LIMIT = 5
DURATION_TOLERANCE_SEC = 8.0
HTTP_TIMEOUT = 6.0

# 控制发送行为
SEND_ON_MATCH = True
MIN_SEND_INTERVAL = 0.05   # 向串口发送最小间隔，防止刷屏（秒）

# 全局变量定义与初始化
playback_anchor_ts = None
playback_anchor_pos = 0.0
_position_watcher_stop = False

_re_hiragana = re.compile(r'[\u3040-\u309F]')
_re_katakana = re.compile(r'[\u30A0-\u30FF]')
_re_hangul = re.compile(r'[\uAC00-\uD7AF]')
_re_cjk = re.compile(r'[\u4E00-\u9FFF\u3400-\u4DBF\uF900-\uFAFF]')
_re_latin = re.compile(r'[A-Za-z]')

# -----------------------
# 全局状态（线程安全）
# -----------------------
app_lock = threading.RLock()
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
current_song = {
    "song_id": None,
    "lyrics": [],   # list of (time_seconds, text)
    "fetched_at": None
}

# 串口发送控制
ser = None
last_sent_ts = 0.0
last_sent_lyric_time = None

# -----------------------
# 工具函数
# -----------------------

def safe_update_state(**kwargs):
  #仅在传入值不为 None 且非空字符串时更新 state,避免意外覆盖已有字段为 None
  with app_lock:
    for k, v in kwargs.items():
        if k in state:
            if v is None or(isinstance(v, str) and v.strip() == ""):
                continue
            state[k] = v
    state['last_update_ts'] = time.time()

#歌词自动输出
def position_watcher(interval=POSITION_WATCH_INTERVAL):
    last_pos = None
    print("position_watcher started, interval=", interval)
    try:
        while True:
            with app_lock:
                if _position_watcher_stop:
                    break
            st = get_state_copy()
            playback = st.get('playback')
            # 只在播放时自动推进
            if playback and str(playback).lower() == 'playing':
                pos = compute_current_position_from_anchor()
                # 只有当 position 明显变化时才调用，避免无谓调用
                if (last_pos is None or abs(pos - last_pos) >= POSITION_CHANGE_THRESHOLD):
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
    with app_lock:
        return dict(state)

def similar(a, b):
    if not a or not b:
        return 0.0
    return SequenceMatcher(None, a.lower(), b.lower()).ratio()

def parse_time_to_seconds(tstr):
    # 支持 hh:mm:ss(.m	s) 或 mm:ss(.ms)
    if not tstr:
        return None
    try:
        parts = tstr.split(':')
        if len(parts) == 3:
            return int(parts[0]) * 3600 + int(parts[1]) * 60 + float(parts[2])
        elif len(parts) == 2:
            return int(parts[0]) * 60 + float(parts[1])
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

#创建lrc歌词组 双语
def build_grouped_lyrics(lrc_text, tlyric_text=None):
    #返回 [(time_seconds, [(source, text), ...]), ...]
    #source 为 'lrc' 或 'tlyric'，保留重复文本并保持加入顺序。
    parsed_main = parse_lrc(lrc_text) if lrc_text else []
    parsed_trans = parse_lrc(tlyric_text) if tlyric_text else []
    grouped = {}   # key -> (orig_time, [(source, text), ...])
    order = []

    def add_entry(t, txt, src):
        if not txt or not txt.strip():
            return
        txt = txt.strip()
        k = round(t, 3)
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

    return [(grouped[k][0], grouped[k][1]) for k in sorted(order)]


#检测当前时间
def set_playback_anchor(pos_seconds, at_ts=None):
    #将播放锚点设置为：在 wall-clock 时间 at_ts 时，歌曲位置为 pos_seconds。
    #如果 at_ts 为 None，使用当前时间.
    global playback_anchor_ts, playback_anchor_pos
    if at_ts is None:
        at_ts = time.time()
    with app_lock:
        playback_anchor_ts = float(at_ts)
        playback_anchor_pos = float(pos_seconds or 0.0)
    # debug
    print("[ANCHOR] set anchor pos=%.3f at_ts=%.3f" % (playback_anchor_pos, playback_anchor_ts))

def compute_current_position_from_anchor():
    #返回基于锚点计算的当前歌曲位置（秒）。
    #逻辑：
    #  - 如果 anchor_ts 不为 None：返回 anchor_pos + (now - anchor_ts)
    #  - 否则返回 anchor_pos（表示暂停时保存的位置）
    if playback_anchor_ts is None:
        return float(playback_anchor_pos or 0.0)
    else:
        return float(playback_anchor_pos + max(0.0, time.time() - playback_anchor_ts))

# -----------------------
# NetEase API 调用
# -----------------------
NETEASE_HEADERS = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
    'Referer': 'https://music.163.com',
    'Accept': 'application/json, text/plain, */*',
}

def netease_search(query, limit=NETEASE_SEARCH_LIMIT):
    #网易云搜索
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
        dur_sec = float(dur_ms) / 1000.0 if dur_ms else None
        # compute similarity score
        s_title = similar(title, t)
        s_artist = similar(artist, ar)
        s_album = similar(album, al)
        # duration score: if duration available, penalize difference
        dur_score = 1.0
        if duration_sec and dur_sec:
            diff = abs(duration_sec - dur_sec)
            dur_score = (
                1.0
                if diff <= DURATION_TOLERANCE_SEC
                else max(0.0, 1.0 - (diff / max(duration_sec, dur_sec, 1.0)))
            )
        # weighted sum
        score = (0.6 * s_title + 0.25 * s_artist + 0.1 * s_album) * dur_score
        # small boost if exact artist substring
        if artist and artist.lower() in ar.lower():
            score += 0.05
        if score > best_score:
            best_score = score
            best = sid
    return best

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
# 依赖：全局变量 ser, app_lock, MIN_SEND_INTERVAL, last_sent_ts 已在脚本中定义
# 这两个函数分别用于发送第一行和第二行文本，线程安全并带节流与可选强制发送参数。
def send_to_serial_line1(text: str, force: bool = False) -> None:
    #逐行发送第一条文本（LRC1 前缀由调用方添加或在上层处理）。
    #参数:text  - 要发送的纯文本（不包含额外换行，函数会自动添加换行）  force - True 时忽略最小发送间隔立即发送
    global last_sent_ts, ser
    if not SEND_ON_MATCH or ser is None:
        return
    now = time.time()
    if not force and (now - last_sent_ts) < MIN_SEND_INTERVAL:
        return
    try:
        with app_lock:
            # 1. 先用干净的 text 检测语言编码
            encoding = simple_detect_line_language(text)
            # 2. 再拼接换行符打包发送
            payload = text + '\n\r'
            ser.write(b'\x0C')
            ser.write(payload.encode(encoding, errors='ignore'))
            ser.flush()
        last_sent_ts = now
    except Exception as e:
        print("Serial send (line1) error:", e)

def send_to_serial_line2(text: str, force: bool = False) -> None:
    #逐行发送第二条文本(LRC2 前缀由调用方添加或在上层处理)。
    #参数与 send_to_serial_line1 相同
    global last_sent_ts, ser
    if not SEND_ON_MATCH or ser is None:
        return
    now = time.time()
    if not force and (now - last_sent_ts) < MIN_SEND_INTERVAL:
        return
    try:
        with app_lock:
            #payload = text
            ser.write(text.encode(simple_detect_line_language(text), errors='ignore'))
            ser.flush()
        last_sent_ts = now
    except Exception as e:
        print("Serial send (line2) error:", e)

# -----------------------
# 管道消息解析逻辑：当收到新管道行时解析并触发动作
# -----------------------
_re_timeline = re.compile(
    r"""^\[.*?\]\s*(?P<src>\S+)\s+timeline\s+is\s+now\s+(?P<pos>\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?)/(?P<dur>\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?)""",
    re.IGNORECASE | re.VERBOSE,
)

def parse_pipe_line(line):
  line = line.strip()
  if not line:
    return

  # 先统一更新 last_line 字段
  safe_update_state(last_line=line)
  lower = line.lower()

  # 1. 明确的暂停状态（例如 "is now Paused"）
  if 'is now paused' in lower:
    safe_update_state(playback='Paused')
    return

  # 2. timeline 行（进度拖动 / 暂停前锁定 / 恢复播放定位）
  m_tl = _re_timeline.match(line)
  if m_tl:
    src = m_tl.group('src')
    pos = parse_time_to_seconds(m_tl.group('pos'))
    dur = parse_time_to_seconds(m_tl.group('dur'))
    safe_update_state(source=src, position=pos, duration=dur)
    # 更新基准锚点
    set_playback_anchor(pos, at_ts=time.time())
    return

  # 3. 播放状态与歌曲信息处理
  if 'is now playing' in lower:
    safe_update_state(playback='Playing')

    # 判断是否带有具体的 "歌名 by 歌手" 信息
    idx = lower.find('is now playing ')
    if idx != -1:
      info_part = line[idx + len('is now playing ') :].strip()

      # 从后往前找最后一个 ' by '，精准拆分歌名和歌手（防歌名含 by）
      by_idx = info_part.rfind(' by ')
      if by_idx != -1:
        new_title = info_part[:by_idx].strip()
        new_artist = info_part[by_idx + 4 :].strip()

        st = get_state_copy()
        # 仅当歌名发生变化时才触发新歌搜索，防止恢复播放时重复请求
        if st.get('title') != new_title:
          safe_update_state(title=new_title, artist=new_artist)
          on_new_song_detected()

# 当检测到新歌曲信息时（title/artist/album 更新）
def on_new_song_detected():
    set_playback_anchor(0.0, at_ts=time.time())
    st = get_state_copy()
    title = st.get('title')
    artist = st.get('artist')
    album = st.get('album')
    duration = st.get('duration')
    if not title:
        return
    # 记录搜索开始时间（线程安全）
    with app_lock:
        current_song['fetch_start'] = time.time()
        current_song['fetch_duration'] = None
        current_song['song_id'] = None
        current_song['lyrics'] = []
        current_song['fetched_at'] = None

    t = threading.Thread(target=search_and_fetch_lyrics, args=(title, artist, album, duration), daemon=True)
    t.start()

# 搜索并获取歌词（会更新 current_song）
def search_and_fetch_lyrics(title, artist, album, duration):
    try:
        print("Searching for:", title, artist, album, duration)
        # 记录请求开始（也可在 choose_best_song_id 内记录更细粒度 RTT）
        sid = choose_best_song_id(title, artist, album, duration)
        print("Searched SongId is "+str(sid))
        if not sid:
            print("No song id found for", title, artist)
            return

        # 获取歌词并测量请求耗时
        #lyric_start = time.time()
        #data, http_rtt = netease_get_lyric(sid)  # netease_get_lyric 返回 (json, rtt)
        res = netease_get_lyric(sid)
        data = netease_get_lyric(sid)
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

        fetch_end = time.time()
        with app_lock:
            current_song['song_id'] = sid
            current_song['lyrics'] = grouped
            current_song['fetched_at'] = fetch_end

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

    # 直接基于锚点计算当前最精确的播放位置
    pos = compute_current_position_from_anchor()
    if pos is None:
        return
    with app_lock:
        grouped = list(current_song.get('lyrics') or [])

    # 统一使用全局偏移量
    target_time = pos + LYRIC_OFFSET

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
    if (
        last_sent_lyric_time is not None
        and abs(last_sent_lyric_time - base_time) < 1e-6
    ):
        return

    # entries 是 [(source, text), ...]，按顺序取前两条文本
    if len(entries) == 1:
        text1 = entries[0][1]
        text2 = ""   # 清空第二行
    else:
        text1 = entries[0][1]
        text2 = entries[1][1]

    # 逐行发送，使用 force=True 确保连续发送
    send_to_serial_line1(text1, force=True)
    send_to_serial_line2(text2, force=True)

    last_sent_lyric_time = base_time
    print(
        'Sent grouped lyric time=%.3f entries=%d pos=%.3f lyric_offset=%.3f'
        % (base_time, len(entries), pos, LYRIC_OFFSET)
    )
    print()

# -----------------------
# 管道读取线程（Windows named pipe via pywin32）
# -----------------------
def pipe_reader_loop(pipe_name):
  print("Pipe reader started:", pipe_name)
  while True:
    handle = None
    try:
      handle = win32file.CreateFile(pipe_name,win32file.GENERIC_READ,0,None,win32file.OPEN_EXISTING,0,None,)
      data = b""
      while True:
        hr, chunk = win32file.ReadFile(handle, 4096)
        if not chunk:
          break
        data += chunk
        while b"\n" in data:
          line, data = data.split(b"\n", 1)
          s = line.decode("utf-8", errors="ignore").strip()
          if s:
            print("[PIPE] " + s)
            parse_pipe_line(s)

    except pywintypes.error as e:
      if e.winerror == 231:
        try:
          win32pipe.WaitNamedPipe(pipe_name, 1000)
        except Exception:
          time.sleep(0.5)
    except Exception as e:
      print("pipe_reader_loop error:", e)
      time.sleep(0.5)
    finally:
      # 保证无论读完、断开还是报错，都能即时释放当前句柄
      if handle:
          win32file.CloseHandle(handle)

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
    com_ports = list(serial.tools.list_ports.comports())
    if not com_ports:
        print('未检测到COM端口')
        os.system('pause')
        sys.exit(0)

    for port in com_ports:
        print(f'名称: {port.name}\n描述: {port.description}\n硬件ID:'f' {port.hwid}\n'+ '-' * 30)
    SERIAL_PORT = input("请输入要连接的COM端口：");
    try:
        open_serial(SERIAL_PORT, SERIAL_BAUD)

    #initalize screen
        ser.write(b'\x0C\x1f\x03')
        #ser.write(b'\x1F\x58\x03')      #set brightness 03-75%
    except Exception as e:
        print("Serial open failed:", e)

    # start pipe or stdin reader
    if PIPE_MODE and PIPE_AVAILABLE:
        threading.Thread(target=pipe_reader_loop, args=(PIPE_NAME,), daemon=True).start()
    else:
        print("PIPE_MODE disabled or pywin32 not available; using stdin")
        threading.Thread(target=stdin_reader_loop, daemon=True).start()

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
        with app_lock:
            _position_watcher_stop = True
        # give watcher a moment to exit
        watcher_thread.join(timeout=1.0)
        if ser:
            ser.close()

# text language test return gb2312 big5 shift_jis ksc5601 or ascii  2026-08-16
def simple_detect_line_language(text):
    if not text or not text.strip():
        return 'ascii'

    text = text.strip()
    counts = {
        'hiragana': 0,
        'katakana': 0,
        'hangul': 0,
        'cjk': 0,
        'latin': 0,
        'other': 0,
  }

    for ch in text:
      code = ord(ch)
      if 0x3040 <= code <= 0x309F:
        counts['hiragana'] += 1
      elif 0x30A0 <= code <= 0x30FF:
        counts['katakana'] += 1
      elif 0xAC00 <= code <= 0xD7AF:
        counts['hangul'] += 1
      elif ((0x4E00 <= code <= 0x9FFF)or (0x3400 <= code <= 0x4DBF)or (0xF900 <= code <= 0xFAFF)):
        counts['cjk'] += 1
      elif ('a' <= ch <= 'z') or ('A' <= ch <= 'Z'):
        counts['latin'] += 1
      else:
        counts['other'] += 1

    if sum(counts.values()) == 0:
      ser.write(b'\x1F\x28\x67\x02\x00')
      return 'ascii'

    print(text)
    print(counts)

    # 1. 假名（日文）
    if counts['hiragana'] + counts['katakana'] > 0:
      ser.write(b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x00')
      print('L is JP')
      return 'shift_jis'

    # 2. 韩文
    if counts['hangul'] > 0 and counts['hangul'] >= counts['cjk']:
      ser.write(b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x01')
      print('L is KR')
      return 'KSC5601'

    # 3. 中文（简体 / 繁体）
    if counts['cjk'] > 0 and counts['cjk'] >= max(counts['hangul'], counts['hiragana'] + counts['katakana']):
      if zhconv.convert(text, 'zh-cn') != text:
        ser.write(b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x03')
        print('L is zhT (Big5)')
        return 'Big5'
      else:
        ser.write(b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x02')
        print('L is zhS (GB2312)')
        return 'GB2312'

    # 4. 拉丁字母（英文）
    if counts['latin'] > 0 and counts['latin'] >= max(counts['cjk'], counts['hangul'], counts['hiragana'] + counts['katakana']):
      ser.write(b'\x1F\x28\x67\x02\x00')
      print('L is EN')
      return 'ascii'

    # 5. 默认 ASCII
    print('L is UnK def ASCII')
    ser.write(b'\x1F\x28\x67\x02\x00')
    return 'ascii'

if __name__ == '__main__':
    main()
