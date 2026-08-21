# -*- coding: utf-8 -*-
"""
抖音弹幕模拟器 —— 模拟 DouyinBarrageGrab 的 WebSocket 推送端。
无需开真直播即可测试 MateEngine 的抖音互动全链路。

用法:
    python douyin_mock_server.py            # 交互模式（推荐）
    python douyin_mock_server.py --auto     # 自动模式：随机持续发事件

交互模式命令:
    c <内容>     发弹幕            例: c 主播今天好可爱
    e [昵称]     观众进房          例: e 小明
    f [昵称]     关注
    s [昵称]     分享
    l [次数]     点赞              例: l 30
    g <礼物名> [数量] [抖币]  礼物  例: g 玫瑰 3 1
    gg           大礼物连击测试（火箭x3，共300抖币）
    song <歌名>  点歌              例: song 孤勇者
    dance [名]   点舞（本地舞蹈库） 例: dance 极乐净土
    auto         切换自动模式开/关
    q            退出
"""
import asyncio, json, random, sys, threading, time

PORT = 8888
for i, a in enumerate(sys.argv):
    if a == "--port" and i + 1 < len(sys.argv):
        PORT = int(sys.argv[i + 1])

NAMES = ["小明", "阿狸", "喵喵酱", "大熊", "月半猫", "柚子", "老王", "冰糖", "泡芙", "七七"]
CHATS = [
    "主播今天好可爱", "这个桌宠叫什么名字呀", "第一次来，主播好",
    "能跳个舞吗", "声音好好听", "已关注！", "主播玩的什么游戏",
    "来了来了", "这只小人是AI吗", "晚上好呀",
]
GIFTS = [("玫瑰", 1, 1), ("抖音", 1, 1), ("小心心", 5, 1), ("棒棒糖", 1, 9), ("爱心飞吻", 1, 99)]

clients = set()
msg_id = int(time.time() * 1000)

def make_user(nick=None):
    nick = nick or random.choice(NAMES)
    uid = abs(hash(nick)) % 10**10
    return {"Id": uid, "ShortId": str(uid)[:8], "DisplayId": f"dy_{uid % 10**6}",
            "Nickname": nick, "Level": random.randint(1, 40), "PayLevel": 0,
            "SecUid": f"MS4wLjABAAAA_mock_{uid}", "FansClubLevel": 0,
            "FollowingStatus": False, "HeadImgUrl": ""}

def envelope(type_, inner):
    global msg_id
    msg_id += 1
    inner.setdefault("MsgId", msg_id)
    inner.setdefault("RoomId", 7350000000000000000)
    return json.dumps({"Type": type_, "Data": json.dumps(inner, ensure_ascii=False)}, ensure_ascii=False)

def ev_chat(content, nick=None):
    return envelope(1, {"User": make_user(nick), "Content": content})

def ev_like(count=10, nick=None):
    return envelope(2, {"User": make_user(nick), "Count": count, "Total": random.randint(100, 99999), "Content": ""})

def ev_enter(nick=None):
    return envelope(3, {"User": make_user(nick), "Content": "来了"})

def ev_follow(nick=None):
    return envelope(4, {"User": make_user(nick), "Content": "关注了主播"})

def ev_gift(name="玫瑰", count=1, diamond=1, nick=None):
    return envelope(5, {"User": make_user(nick), "Content": f"送出了{name}",
                        "GiftId": abs(hash(name)) % 10**6, "GiftName": name,
                        "GiftCount": count, "GroupCount": 1, "RepeatCount": count,
                        "DiamondCount": diamond})

def ev_share(nick=None):
    return envelope(8, {"User": make_user(nick), "Content": "分享了直播间"})

async def broadcast(payload, desc):
    if not clients:
        print(f"  !! 没有客户端连接（MateEngine 未连上），事件丢弃: {desc}")
        return
    for ws in list(clients):
        try:
            await ws.send(payload)
        except Exception:
            clients.discard(ws)
    print(f"  -> 已发送: {desc}  (客户端数: {len(clients)})")

async def handler(ws):
    peer = getattr(ws, "remote_address", "?")
    clients.add(ws)
    print(f"[连接] MateEngine 已接入 {peer}（当前 {len(clients)} 个客户端）")
    try:
        async for _ in ws:  # 不期望收到消息，保持连接
            pass
    except Exception:
        pass
    finally:
        clients.discard(ws)
        print(f"[断开] {peer}")

AUTO = "--auto" in sys.argv

SONGS = ["孤勇者", "晴天", "起风了", "小苹果", "爱你"]
DANCES = ["", "", "极乐净土"]  # 空=随机

async def auto_loop():
    while True:
        await asyncio.sleep(random.uniform(2.5, 6.0))
        if not AUTO:
            continue
        roll = random.random()
        if roll < 0.30:
            await broadcast(ev_chat(random.choice(CHATS)), "弹幕")
        elif roll < 0.50:
            await broadcast(ev_enter(), "进房")
        elif roll < 0.65:
            await broadcast(ev_like(random.randint(5, 40)), "点赞")
        elif roll < 0.78:
            g = random.choice(GIFTS)
            await broadcast(ev_gift(*g), f"礼物 {g[0]}")
        elif roll < 0.84:
            await broadcast(ev_gift("火箭", 1, random.choice([100, 300])), "大礼物 火箭")
        elif roll < 0.90:
            await broadcast(ev_follow(), "关注")
        elif roll < 0.95:
            kw = random.choice(SONGS)
            await broadcast(ev_chat("点歌 " + kw), f"自动点歌 {kw}")
        else:
            kw = random.choice(DANCES)
            await broadcast(ev_chat(("点舞 " + kw).strip()), f"自动点舞 {kw or '(随机)'}")

def input_thread(loop):
    global AUTO
    help_short = "命令: c <弹幕> | e 进房 | f 关注 | s 分享 | l [数] 点赞 | g <礼物> [数] [抖币] | gg 大礼物 | song <歌名> | dance [舞名] | auto | q"
    print(help_short)
    while True:
        try:
            line = input("> ").strip()
        except (EOFError, KeyboardInterrupt):
            # stdin 不可用（如后台运行）：停用交互输入，但服务器继续运行
            print("[输入线程退出，服务器继续运行，Ctrl+C 结束]")
            return
        if not line:
            continue
        parts = line.split()
        cmd, args = parts[0].lower(), parts[1:]
        if cmd == "q":
            print("退出")
            loop.call_soon_threadsafe(loop.stop)
            return
        elif cmd == "auto":
            AUTO = not AUTO
            print(f"自动模式: {'开' if AUTO else '关'}")
            continue
        elif cmd == "c":
            payload, desc = ev_chat(" ".join(args) or random.choice(CHATS)), "弹幕"
        elif cmd == "e":
            payload, desc = ev_enter(args[0] if args else None), "进房"
        elif cmd == "f":
            payload, desc = ev_follow(args[0] if args else None), "关注"
        elif cmd == "s":
            payload, desc = ev_share(args[0] if args else None), "分享"
        elif cmd == "l":
            payload, desc = ev_like(int(args[0]) if args else 10), "点赞"
        elif cmd == "g":
            name = args[0] if args else "玫瑰"
            count = int(args[1]) if len(args) > 1 else 1
            diamond = int(args[2]) if len(args) > 2 else 1
            payload, desc = ev_gift(name, count, diamond), f"礼物 {name}x{count}"
        elif cmd == "gg":
            payload, desc = ev_gift("火箭", 3, 100), "大礼物 火箭x3(300抖币)"
        elif cmd == "song":
            kw = " ".join(args) or "孤勇者"
            payload, desc = ev_chat("点歌 " + kw), f"点歌 {kw}"
        elif cmd == "dance":
            kw = " ".join(args)
            payload, desc = ev_chat(("点舞 " + kw).strip()), f"点舞 {kw or '(随机)'}"
        else:
            print(help_short)
            continue
        asyncio.run_coroutine_threadsafe(broadcast(payload, desc), loop)

async def main():
    import websockets
    print(f"=== 抖音弹幕模拟器 ===  ws://127.0.0.1:{PORT}")
    print("等待 MateEngine 连接…（确保真正的 DouyinBarrageGrab 已关闭，避免端口冲突）")
    if AUTO:
        print("自动模式已开启")
    loop = asyncio.get_running_loop()
    threading.Thread(target=input_thread, args=(loop,), daemon=True).start()
    async with websockets.serve(handler, "127.0.0.1", PORT):
        asyncio.create_task(auto_loop())
        await asyncio.Future()

if __name__ == "__main__":
    try:
        asyncio.run(main())
    except (KeyboardInterrupt, RuntimeError):
        pass
