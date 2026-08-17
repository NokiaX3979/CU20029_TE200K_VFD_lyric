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
    import win32file, win32pipe, pywintypes  # 已补全 win32pipe 导入
    PIPE_AVAILABLE = True
except Exception:
    PIPE_AVAILABLE = False

# -----------------------
# 配置参数
# -----------------------
PIPE_MODE = True  # True: read from named pipe; False: read from stdin
PIPE_NAME = r'\\.\pipe\MusicInfoPipe'
SERIAL_BAUD = 38400

# 滚屏与显示参数
DISPLAY_WIDTH = 20          # VFD 屏幕每行字符数
DEFAULT_PAUSE_SEC = 0.6     # 动态滚屏首尾默认停顿时间（秒）

# 自动轮询渲染帧的间隔（秒，0.08s 约为 12 FPS，保障滚屏丝滑且 CPU 占用低）
POSITION_WATCH_INTERVAL = 0.06     
LYRIC_OFFSET = 0.0          # 全局统一歌词偏移量（秒）：正数提前，负数延后

NETEASE_SEARCH_LIMIT = 5
DURATION_TOLERANCE_SEC = 8.0
HTTP_TIMEOUT = 6.0

# VFD 光标定位指令
CMD_LINE1_START = b'\x1F\x24\x01\x01'  # 定位到 Line 1 起点
CMD_LINE2_START = b'\x1F\x24\x01\x02'  # 定位到 Line 2 起点
VFD_HMODE = True
# 全局变量定义与初始化
playback_anchor_ts = None
playback_anchor_pos = 0.0
_position_watcher_stop = False

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
    "last_line": None,  
    "last_update_ts": None
}

# 歌词缓存与当前歌曲信息
current_song = {
    "song_id": None,
    "lyrics": [],   # list of (time_seconds, [(source, text), ...])
    "fetched_at": None
}

# 渲染与滚屏缓存状态
active_lyric_state = {
    "base_time": None,
    "text1": "",
    "text2": "",
    "enc1": "ascii",
    "enc2": "ascii",
    "last_slice1": None,
    "last_slice2": None
}

ser = None

# -----------------------
# 工具函数
# -----------------------

def safe_update_state(**kwargs):
    with app_lock:
        for k, v in kwargs.items():
            if k in state:
                if v is None or (isinstance(v, str) and v.strip() == ""):
                    continue
                state[k] = v
        state['last_update_ts'] = time.time()

def get_state_copy():
    with app_lock:
        return dict(state)

def similar(a, b):
    if not a or not b:
        return 0.0
    return SequenceMatcher(None, a.lower(), b.lower()).ratio()

def parse_time_to_seconds(tstr):
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
    lines.sort(key=lambda x: x[0])
    return lines

def build_grouped_lyrics(lrc_text, tlyric_text=None):
    parsed_main = parse_lrc(lrc_text) if lrc_text else []
    parsed_trans = parse_lrc(tlyric_text) if tlyric_text else []
    grouped = {}   
    order = []

    def add_entry(t, txt, src):
        if not txt or not txt.strip():
            return
        txt = txt.strip()
        k = round(t, 3)
        if k not in grouped:
            grouped[k] = [t, []]
            order.append(k)
        grouped[k][1].append((src, txt))

    for t, txt in parsed_main:
        add_entry(t, txt, 'lrc')
    for t, txt in parsed_trans:
        add_entry(t, txt, 'tlyric')

    return [(grouped[k][0], grouped[k][1]) for k in sorted(order)]

def set_playback_anchor(pos_seconds, at_ts=None):
    global playback_anchor_ts, playback_anchor_pos
    if at_ts is None:
        at_ts = time.time()
    with app_lock:
        playback_anchor_ts = float(at_ts)
        playback_anchor_pos = float(pos_seconds or 0.0)

def compute_current_position_from_anchor():
    if playback_anchor_ts is None:
        return float(playback_anchor_pos or 0.0)
    else:
        return float(playback_anchor_pos + max(0.0, time.time() - playback_anchor_ts))

def send_single_line_lyric(text: str):
    """
    单行无翻译歌词直推送：清屏 + 设置 Vertical scroll mode + 一次性发送文本
    由 VFD 硬件自动在 20 字符处换行到第二行
    """
    cmd, enc = simple_detect_line_language(text)
    payload = text.encode(enc, errors='replace')
    # 指令序列：0Ch (清屏复位) + 切码指令 + 歌词文本
    ser.write(b'\x0C' + cmd + payload)
    ser.flush()
    print('Single push:', text)

# -----------------------
# 语言检测与硬件切码（完整匹配 中/繁/日/韩/英）
# -----------------------
def simple_detect_line_language(text):
    """
    通过字符特征与编码瀑布流（GB2312 -> Big5 -> Shift_JIS -> KSC5601），
    精确匹配 VFD 硬件支持的语言。
    """
    if not text or not text.strip():
        return (b'\x1F\x28\x67\x02\x00', 'ascii')

    text = text.strip()
    counts = {'hiragana': 0, 'katakana': 0, 'hangul': 0, 'cjk': 0}

    for ch in text:
        code = ord(ch)
        if 0x3040 <= code <= 0x309F:
            counts['hiragana'] += 1
        elif 0x30A0 <= code <= 0x30FF:
            counts['katakana'] += 1
        elif 0xAC00 <= code <= 0xD7AF:
            counts['hangul'] += 1
        elif (0x4E00 <= code <= 0x9FFF) or (0x3400 <= code <= 0x4DBF) or (0xF900 <= code <= 0xFAFF):
            counts['cjk'] += 1

    # 1. 带假名的日文 (100% 确定为日文)
    if counts['hiragana'] + counts['katakana'] > 0:
        print("L: 100%JP")
        return (b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x00', 'shift_jis')


    # 2. 带谚文的韩文 (100% 确定为韩文)
    if counts['hangul'] > 0:
        print("L: 100%KR")
        return (b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x01', 'ksc5601')


    # 3. 无假名/谚文的 CJK 纯汉字（按字库优先级碰撞）
    if counts['cjk'] > 0:
        # ① 优先测试简体中文 (GB2312)
        try:
            text.encode('gb2312')
            print("L: zhS OK")
            return (b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x02', 'GB2312')
        except UnicodeEncodeError:
            pass

        # ② 测试繁体中文 (Big5)
        try:
            text.encode('Big5')
            print("L: zhT OK")
            return (b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x03', 'Big5')
        except UnicodeEncodeError:
            pass

        # ③ 测试日文汉字 (Shift_JIS，可捕获“毎”、“桜”、“転”等日文独有汉字)
        try:
            text.encode('Shift_JIS')
            print("L: JP OK")
            return (b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x00', 'shift_jis')
        except UnicodeEncodeError:
            pass

        # ④ 测试韩文汉字 (KSC5601 / EUC-KR)
        try:
            text.encode('KSC5601')
            print("L: KR OK")
            return (b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x01', 'ksc5601')
        except UnicodeEncodeError:
            pass

    # 4. ASCII / 纯英文降级处理
    try:
        text.encode('ascii')
        print("L: def ASC")
        return (b'\x1F\x28\x67\x02\x00', 'ASCII')
    except UnicodeEncodeError:
        # 若夹杂全角标点导致 ASCII 失败，进行传统zhconv转换，并强制转换为繁体中文
        if zhconv.convert(text, 'zh-cn') != text:
          print("L: Downgrade zhT")
          text = zhconv.convert(text, 'zh-tw')
          return (b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x03', 'Big5')
        else:
          print("L: Downgrade zhS")
          return (b'\x1F\x28\x67\x02\x01\x1F\x28\x67\x03\x02', 'GB2312')

# -----------------------
# 辅助函数：清洗全角符号与安全编码
# -----------------------
def safe_encode_slice(slice_text: str, encoding: str) -> bytes:
    """
    确保切片固定为 DISPLAY_WIDTH(20) 个字符；
    若为 Big5 编码，自动将混入的简体字转为标准繁体字（如 '着' -> '著'）。
    """
    replacements = {
        '\u3000': ' ',   # 全角空格
        '\t': ' ',       # 制表符
        '—': '-',        # 破折号
        '…': '...',      # 省略号
        '～': '~',
        ' ': ''	,		 #神秘空格(NO-BREAK SPACE)
        '•': '●',
    }
    for old_c, new_c in replacements.items():
        slice_text = slice_text.replace(old_c, new_c)

    # 如果确定使用 Big5 编码，将整行统一转为台湾繁体 (zh-tw)，消除混入的简体字
    if encoding.lower() in ('big5', 'big5hkscs'):
        slice_text = zhconv.convert(slice_text, 'zh-tw')

    padded_text = slice_text[:DISPLAY_WIDTH].ljust(DISPLAY_WIDTH)
    return padded_text.encode(encoding, errors='replace')

# -----------------------
# 动态滚屏偏移量计算引擎（保留单一定义）
# -----------------------
def get_scroll_offset(text: str, elapsed_time: float, duration: float = 5.0) -> int:
    """
    根据歌词可用时长，动态计算当前滚屏偏移字符数
    """
    if not text:
        return 0

    text_len = len(text)
    max_offset = text_len - DISPLAY_WIDTH
    if max_offset <= 0:
        return 0  # 未超过 20 字符，无需滚动

    # 动态适配首尾停顿（默认各 DEFAULT_PAUSE_SEC；若总时长不足 1.6s，则各占总时长的 20%）
    pause_start = min(DEFAULT_PAUSE_SEC, duration * 0.2)
    pause_end = min(DEFAULT_PAUSE_SEC, duration * 0.2)
    active_time = duration - pause_start - pause_end

    # 时长极短时的防碰撞处理
    if active_time <= 0:
        progress = min(1.0, max(0.0, elapsed_time / duration)) if duration > 0 else 0
        return min(max_offset, int(progress * (max_offset + 1)))

    # 1. 开头停顿阶段
    if elapsed_time < pause_start:
        return 0

    if elapsed_time >= (duration - pause_end):
        return max_offset

    scroll_elapsed = elapsed_time - pause_start
    progress = scroll_elapsed / active_time
    progress = min(1.0, max(0.0, progress))

    return min(max_offset, int(progress * (max_offset + 1)))

# -----------------------
# 核心渲染与发送引擎
# -----------------------
def compute_and_send_current_lyric():
    global VFD_HMODE
    if ser is None:
        return

    st = get_state_copy()
    if not st.get('title') or (st.get('playback') and str(st.get('playback')).lower() == 'paused'):
        return

    pos = compute_current_position_from_anchor()
    if pos is None:
        return

    # 1. 从 current_song 中获取歌词列表和整曲 has_tlyric 标记
    with app_lock:
        grouped = list(current_song.get('lyrics') or [])
        song_has_tlyric = current_song.get('has_tlyric', False)

    target_time = pos + LYRIC_OFFSET

    # 匹配当前时间点对应的歌词句
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

    # =================【分支 A：整曲无翻译 -> 单行全屏直推】=================
    if not song_has_tlyric:
        if base_time != active_lyric_state["base_time"]:
            active_lyric_state["base_time"] = base_time
            text_single = entries[0][1]
            if VFD_HMODE:
                ser.write(b'\x1F\x02')  # 切换至 Vertical scroll mode
                ser.flush()
                VFD_HMODE = False

            send_single_line_lyric(text_single)
        return  # 单行直推只需在时间戳变化时发送一次，之后直接退出

    # =================【分支 B：整曲有翻译 -> 双行滚屏推送】=================
    text1 = entries[0][1] if len(entries) >= 1 else ""
    text2 = entries[1][1] if len(entries) >= 2 else ""
    
    # 动态切换回 Horizontal scroll mode
    if not VFD_HMODE:
        ser.write(b'\x1F\x03')
        ser.flush()
        VFD_HMODE = True
    # 计算当前歌词句的动态可用时长 duration
    if idx + 1 < len(grouped):
        next_time = grouped[idx + 1][0]
        duration = max(0.5, next_time - base_time)
    else:
        duration = 5.0

    if base_time != active_lyric_state["base_time"]:
        active_lyric_state["base_time"] = base_time
        active_lyric_state["text1"] = text1
        active_lyric_state["text2"] = text2
        
        active_lyric_state["cmd1"], active_lyric_state["enc1"] = simple_detect_line_language(text1)
        active_lyric_state["cmd2"], active_lyric_state["enc2"] = simple_detect_line_language(text2)

        active_lyric_state["last_slice1"] = None
        active_lyric_state["last_slice2"] = None

    elapsed_time = max(0.0, target_time - base_time)

    # 1. 第一行动态滚屏计算
    off1 = get_scroll_offset(active_lyric_state["text1"], elapsed_time, duration)
    raw_slice1 = active_lyric_state["text1"][off1 : off1 + DISPLAY_WIDTH]

    if raw_slice1 != active_lyric_state["last_slice1"]:
        payload1 = safe_encode_slice(raw_slice1, active_lyric_state["enc1"])
        ser.write(active_lyric_state["cmd1"] + CMD_LINE1_START)
        ser.write(payload1)
        print('line1: ' + raw_slice1)
        ser.flush()
        active_lyric_state["last_slice1"] = raw_slice1

    # 2. 第二行动态滚屏计算
    off2 = get_scroll_offset(active_lyric_state["text2"], elapsed_time, duration)
    raw_slice2 = active_lyric_state["text2"][off2 : off2 + DISPLAY_WIDTH]

    if raw_slice2 != active_lyric_state["last_slice2"]:
        payload2 = safe_encode_slice(raw_slice2, active_lyric_state["enc2"])
        ser.write(active_lyric_state["cmd2"] + CMD_LINE2_START)
        ser.write(payload2)
        print('line2: ' + raw_slice2)
        ser.flush()
        active_lyric_state["last_slice2"] = raw_slice2

def position_watcher(interval=POSITION_WATCH_INTERVAL):
    """时间驱动的轮询渲染线程"""
    print("position_watcher started, interval=", interval)
    try:
        while True:
            with app_lock:
                if _position_watcher_stop:
                    break
            st = get_state_copy()
            playback = st.get('playback')
            if playback and str(playback).lower() == 'playing':
                pos = compute_current_position_from_anchor()
                safe_update_state(position=pos)
                try:
                    compute_and_send_current_lyric()
                except Exception as e:
                    print("position_watcher compute error:", e)
            time.sleep(interval)
    except Exception as e:
        print("position_watcher fatal error:", e)

# -----------------------
# NetEase API 调用
# -----------------------
NETEASE_HEADERS = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
    'Referer': 'https://music.163.com',
    'Accept': 'application/json, text/plain, */*',
}

def netease_search(query, limit=NETEASE_SEARCH_LIMIT):
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

def choose_best_song_id(title, artist, album, duration_sec, search_limit=NETEASE_SEARCH_LIMIT):
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
        dur_ms = c.get('duration') or c.get('dt') or None
        dur_sec = float(dur_ms) / 1000.0 if dur_ms else None
        
        s_title = similar(title, t)
        s_artist = similar(artist, ar)
        s_album = similar(album, al)
        dur_score = 1.0
        if duration_sec and dur_sec:
            diff = abs(duration_sec - dur_sec)
            dur_score = (1.0 if diff <= DURATION_TOLERANCE_SEC else max(0.0, 1.0 - (diff / max(duration_sec, dur_sec, 1.0))))
        score = (0.6 * s_title + 0.25 * s_artist + 0.1 * s_album) * dur_score
        if artist and artist.lower() in ar.lower():
            score += 0.05
        if score > best_score:
            best_score = score
            best = sid
    return best

def on_new_song_detected():
    set_playback_anchor(0.0, at_ts=time.time())
    st = get_state_copy()
    title = st.get('title')
    artist = st.get('artist')
    album = st.get('album')
    duration = st.get('duration')
    if not title:
        return
    with app_lock:
        current_song['song_id'] = None
        current_song['lyrics'] = []
        current_song['fetched_at'] = None

    t = threading.Thread(target=search_and_fetch_lyrics, args=(title, artist, album, duration), daemon=True)
    t.start()

def search_and_fetch_lyrics(title, artist, album, duration):
    try:
        print("Searching for:", title, artist, album, duration)
        sid = choose_best_song_id(title, artist, album, duration)
        print("Searched SongId is "+str(sid))
        if not sid:
            return
        data = netease_get_lyric(sid)
        if not data:
            return

        st = get_state_copy()
        if st.get('playback') and str(st.get('playback')).lower() == 'playing':
            pos = st.get('position')
            set_playback_anchor(pos if pos is not None else 0.0, at_ts=time.time())

        lrc_text = data.get('lrc', {}).get('lyric') or ""
        tlyric_text = data.get('tlyric', {}).get('lyric') or ""

        # 1. 确定整曲级别的翻译标记
        has_tlyric = bool(tlyric_text and tlyric_text.strip())

        if not lrc_text and has_tlyric:
            lrc_text = tlyric_text
            has_tlyric = False

        if not lrc_text.strip():
            return

        # 2. 构建歌词（若无翻译则不向 build_grouped_lyrics 传入 tlyric）
        grouped = build_grouped_lyrics(lrc_text, tlyric_text if has_tlyric else None)
        fetch_end = time.time()
        
        # 3. 写入全局状态，记录 has_tlyric
        with app_lock:
            current_song['song_id'] = sid
            current_song['lyrics'] = grouped
            current_song['has_tlyric'] = has_tlyric  # <--- 存入整曲标记
            current_song['fetched_at'] = fetch_end

        print("Fetched grouped lyrics entries:", len(grouped), "has_tlyric:", has_tlyric)
    except Exception as e:
        print("search_and_fetch_lyrics error:", e)

# -----------------------
# 管道消息解析逻辑
# -----------------------
_re_timeline = re.compile(
    r"""^\[.*?\]\s*(?P<src>\S+)\s+timeline\s+is\s+now\s+(?P<pos>\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?)/(?P<dur>\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?)""",
    re.IGNORECASE | re.VERBOSE,
)

def parse_pipe_line(line):
    line = line.strip()
    if not line:
        return

    safe_update_state(last_line=line)
    lower = line.lower()

    if 'is now paused' in lower:
        safe_update_state(playback='Paused')
        return

    m_tl = _re_timeline.match(line)
    if m_tl:
        src = m_tl.group('src')
        pos = parse_time_to_seconds(m_tl.group('pos'))
        dur = parse_time_to_seconds(m_tl.group('dur'))
        safe_update_state(source=src, position=pos, duration=dur)
        set_playback_anchor(pos, at_ts=time.time())
        return

    if 'is now playing' in lower:
        safe_update_state(playback='Playing')
        idx = lower.find('is now playing ')
        if idx != -1:
            info_part = line[idx + len('is now playing ') :].strip()
            by_idx = info_part.rfind(' by ')
            if by_idx != -1:
                new_title = info_part[:by_idx].strip()
                new_artist = info_part[by_idx + 4 :].strip()

                st = get_state_copy()
                if st.get('title') != new_title:
                    safe_update_state(title=new_title, artist=new_artist)
                    on_new_song_detected()

# -----------------------
# 管道读取与主逻辑
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

def pipe_reader_loop(pipe_name):
    print("Pipe reader started:", pipe_name)
    while True:
        handle = None
        try:
            handle = win32file.CreateFile(pipe_name, win32file.GENERIC_READ, 0, None, win32file.OPEN_EXISTING, 0, None)
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
            if handle:
                win32file.CloseHandle(handle)

def stdin_reader_loop():
    print("stdin reader started")
    for raw in sys.stdin:
        s = raw.strip()
        if s:
            print("[STDIN] " + s)
            parse_pipe_line(s)

def main():
    global ser, _position_watcher_stop
    
    com_ports = list(serial.tools.list_ports.comports())
    if not com_ports:
        print('未检测到COM端口')
        os.system('pause')
        sys.exit(0)

    for port in com_ports:
        print(f'名称: {port.name}\n描述: {port.description}\n硬件ID: {port.hwid}\n' + '-' * 30)
    
    SERIAL_PORT = input("请输入要连接的COM端口：")
    try:
        open_serial(SERIAL_PORT, SERIAL_BAUD)
        if ser:
            ser.write(b'\x0C\x1f\x03')
            #ser.write(b'\x1F\x58\x03')      #set brightness 03-75%
    except Exception as e:
        print("Serial open failed:", e)

    if PIPE_MODE and PIPE_AVAILABLE:
        threading.Thread(target=pipe_reader_loop, args=(PIPE_NAME,), daemon=True).start()
    else:
        print("PIPE_MODE disabled or pywin32 not available; using stdin")
        threading.Thread(target=stdin_reader_loop, daemon=True).start()

    watcher_thread = threading.Thread(target=position_watcher, daemon=True)
    watcher_thread.start()

    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        print("exiting...")
        with app_lock:
            _position_watcher_stop = True
        watcher_thread.join(timeout=1.0)
        if ser:
            ser.close()

if __name__ == '__main__':
    main()
