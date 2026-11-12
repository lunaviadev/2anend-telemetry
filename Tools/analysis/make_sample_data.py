#!/usr/bin/env python3
"""Writes synthetic sessions in exactly the format the game logs, for trying the analysis
before there's real playtest data (or for checking analyse.py after a change).

Every session is marked study = "synthetic sample", and the report puts a banner on
anything built from it. Don't submit charts made from this as playtest evidence.

    python make_sample_data.py              8 sessions -> sample_logs/
    python make_sample_data.py -n 20 --out somewhere
"""
import argparse
import datetime
import json
import math
import os
import random
import uuid

ALPHA = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"
SONGS = [("rez_basics", "medium", 79.3, 1.6), ("digipop", "medium", 64.0, 2.1), ("riff_wildfire", "hard", 76.9, 2.6), ("band_showcase", "hard", 70.7, 2.9)]
FLAGS = ["intro_done", "met_rez", "training_done", "bought_first_upgrade", "beat_REZ", "beat_VERSE", "beat_RIFF", "beat_LYRIC", "beat_ARIA", "hd_invite"]
OBJECTIVES = ["Talk to REZ by the fountain", "Finish training with REZ", "Buy your first upgrade at SHOP", "Beat REZ", "Find VERSE on the west side",
              "Find RIFF at the reflecting pool", "Beat LYRIC", "Find ARIA at the east portal", "Get to the HYPER DOME"]
GPUS = [("NVIDIA GeForce RTX 3060", 1.0), ("AMD Radeon RX 6600", 0.9), ("Intel(R) Iris(R) Xe Graphics", 0.45), ("NVIDIA GeForce GTX 1650", 0.6)]


class Writer:
    def __init__(self, f, sid, build, start):
        self.f, self.sid, self.build, self.start, self.seq, self.t, self.scene = f, sid, build, start, 0, 0.0, "MainMenu"

    def log(self, typ, **data):
        self.seq += 1
        utc = (self.start + datetime.timedelta(seconds=self.t)).strftime("%Y-%m-%dT%H:%M:%S.") + "%03dZ" % random.randint(0, 999)
        self.f.write(json.dumps({"session_id": self.sid, "seq": self.seq, "type": typ, "t": round(self.t, 3), "utc": utc,
                                 "scene": self.scene, "build": self.build, "data": data}, separators=(",", ":")) + "\n")


def code_for(sid):
    h = 14695981039346656037
    for ch in sid:
        h ^= ord(ch)
        h = (h * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    out = ""
    for i in range(6):
        if i == 4:
            out += "-"
        out += ALPHA[h % len(ALPHA)]
        h //= len(ALPHA)
    return out


def perf_window(w, power, scene, minutes):
    base = {"MainMenu": 140, "Overworld": 72, "Spire": 64, "Arcade": 110}.get(scene, 90) * power
    fps = max(12, random.gauss(base, base * 0.05))
    low = fps * random.uniform(0.55, 0.85) * (0.6 if scene == "Overworld" and random.random() < 0.15 else 1)
    gpu = 1000 / fps * (0.9 if scene in ("Overworld", "Spire") else 0.5)
    w.log("perf", frames=int(fps * 5), fps=round(fps, 1), avg_ms=round(1000 / fps, 2), low1_fps=round(low, 1), low01_fps=round(low * 0.8, 1),
          p50_ms=round(1000 / fps * 0.97, 2), p95_ms=round(1000 / low * 0.8, 2), p99_ms=round(1000 / low, 2), max_ms=round(1000 / low * 1.6, 2),
          hitches=random.choice([0, 0, 0, 1]), cpu_ms=round(1000 / fps * 0.7, 2), main_ms=round(1000 / fps * 0.6, 2), render_ms=round(1000 / fps * 0.3, 2),
          gpu_ms=round(gpu, 2), bound="gpu" if gpu > 1000 / fps * 0.63 else "cpu",
          mem_alloc_mb=round(620 + minutes * 4 + random.uniform(-15, 15), 1), mem_reserved_mb=900.0, mono_mb=round(80 + minutes, 1),
          gc_collections=random.choice([0, 0, 1]), timescale=1.0)


def battle(w, song, chart, length, density, skill, offset, attempt):
    w.scene = "Overworld"
    w.log("battle_start", song=song, chart=chart, attempt=attempt, enemy=song.split("_")[0].upper(), arcade=False, tutorial=False,
          bpm=120, notes=int(length * density), audio_offset_ms=0.0)
    perfect = good = miss = combo = maxc = 0
    hp = 100
    st = 0.0
    # one hard stretch per song, around 60% of the way through
    hard_at = length * 0.6
    while st < length and hp > 0:
        st += random.expovariate(density)
        lane = random.choices([0, 1, 2, 3], [1, 1.1, 1.1, 0.8])[0]
        difficulty = 1.0 + (2.2 if abs(st - hard_at) < 4 else 0) + (0.5 if lane == 3 else 0)
        err = random.gauss(offset, 38 * difficulty / skill)
        if abs(err) <= 50:
            res = "perfect"
        elif abs(err) <= 120:
            res = "good"
        else:
            res = "miss"
        if res == "miss":
            miss += 1
            combo = 0
            hp -= 6
        else:
            perfect += res == "perfect"
            good += res == "good"
            combo += 1
            maxc = max(maxc, combo)
        w.t += 1 / density
        w.log("judge", song=song, chart=chart, lane=lane, note="tap", result=res, ms=None if res == "miss" else round(err, 2),
              song_t=round(st, 2), combo_before=combo, hp=hp)
        if random.random() < 0.005:
            w.log("hitch", ms=round(random.uniform(55, 140), 1), gc_kb=random.choice([0, 0, 2048]), gc_ran=random.random() < 0.3,
                  loading=False, ctx={"song": song, "song_t": round(st, 2)})
    total = perfect + good + miss
    acc = 100 * (perfect + good * 0.5) / max(1, total)
    outcome = "Win" if hp > 0 else "Lose"
    w.log("battle_end", song=song, chart=chart, outcome=outcome, score=perfect * 300 + good * 100, max_combo=maxc,
          accuracy=round(acc, 2), grade="S" if acc > 95 else "A" if acc > 88 else "B" if acc > 78 else "C",
          perfect=perfect, good=good, miss=miss, total=total, duration_s=round(st + 4, 1), song_t=round(st, 2),
          player_hp=max(0, hp), enemy_hp=0 if outcome == "Win" else 40, arcade=False, tutorial=False)
    return outcome == "Win"


def walk(w, seconds, hotspots):
    x, z = random.choice(hotspots)
    pts = []
    for _ in range(int(seconds)):
        hx, hz = random.choice(hotspots)
        x += (hx - x) * 0.08 + random.gauss(0, 1.2)
        z += (hz - z) * 0.08 + random.gauss(0, 1.2)
        w.t += 1
        pts.append([round(w.t, 1), round(x, 2), 0.22, round(z, 2)])
        if len(pts) == 10:
            w.log("path", ch="overworld", pts=pts)
            pts = []


def session(out, i, start):
    sid = uuid.uuid4().hex
    build = random.choice(["0.1.0+3fa9c21", "0.1.0+3fa9c21", "0.1.1+b71e0d4"])
    gpu, power = random.choice(GPUS)
    if build.startswith("0.1.1"):
        power *= 1.12  # the newer build is a bit faster
    player = uuid.uuid4().hex[:12]
    skill = random.uniform(0.8, 1.5)
    offset = random.gauss(14, 10)  # most setups hit a little late
    path = os.path.join(out, "%s_%s.jsonl" % (start.strftime("%Y%m%d_%H%M%S"), code_for(sid)))
    with open(path, "w", encoding="utf-8") as f:
        w = Writer(f, sid, build, start)
        w.log("session_start", study="synthetic sample", telemetry_version="1.0.0", build=build, player_id=player, session_code=code_for(sid),
              product="2ANENDUNITY", unity="6000.5.6f1", platform="WindowsPlayer", editor=False, dev_build=False, os="Windows 11 (10.0.26200) 64bit",
              device_type="Desktop", cpu="AMD Ryzen 5 5600X 6-Core Processor", cpu_cores=12, cpu_mhz=3700, ram_mb=16384, gpu=gpu, gpu_vendor="",
              gpu_api="Direct3D11", vram_mb=8192, shader_level=50, screen="1920x1080", display="1920x1080", refresh_hz=144.0,
              fullscreen="FullScreenWindow", quality="High", vsync=0, target_fps=-1, frame_timing=True, language="English")
        w.log("consent", granted=True, source="prompt")
        w.log("game_settings", audio_offset_ms=0.0, note_speed=8.0, difficulty="medium", master_volume=1.0, options={})
        minutes = 0.0
        hotspots = [(0, 0), (22, -8), (-30, 12), (8, 34), (-12, -26)]
        reach = random.randint(3, len(FLAGS))
        for k, flag in enumerate(FLAGS[:reach]):
            w.scene = "Overworld"
            if k < len(OBJECTIVES):
                w.log("objective", text=OBJECTIVES[k], prev_secs=0.0)
            if k == 0:
                w.log("span", name="load", ms=round(random.uniform(1800, 3400), 1), to="Overworld", scene="Overworld")
                w.log("scene", **{"from": "MainMenu", "to": "Overworld"})
            stuck = 160 if OBJECTIVES[min(k, len(OBJECTIVES) - 1)].startswith("Find RIFF") else 60
            for _ in range(random.randint(2, 4)):
                walk(w, random.uniform(stuck * 0.5, stuck * 1.5) / 3, hotspots)
                minutes = w.t / 60
                perf_window(w, power, "Overworld", minutes)
            npc = random.choice(["REZ", "VERSE", "SHOP", "VERN", "RIFF"])
            w.log("talk_start", npc=npc)
            w.t += random.uniform(3, 25)
            w.log("talk_end", npc=npc, secs=round(random.uniform(3, 25), 1))
            if k >= 4 and k - 4 < len(SONGS):
                song, chart, length, density = SONGS[k - 4]
                attempt = 1
                while not battle(w, song, chart, length, density, skill, offset, attempt) and attempt < 4:
                    attempt += 1
                perf_window(w, power, "Overworld", w.t / 60)
            w.log("flag", flag=flag, on=True)
        w.log("session_end", reason="quit", duration_s=round(w.t, 1), events=w.seq + 1, perf={}, hitches=0, peak_mem_mb=700.0, counts={})
    return path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=8)
    ap.add_argument("--out", default="sample_logs")
    ap.add_argument("--seed", type=int, default=2)
    a = ap.parse_args()
    random.seed(a.seed)
    os.makedirs(a.out, exist_ok=True)
    start = datetime.datetime(2026, 11, 10, 13, 0, 0)
    for i in range(a.n):
        p = session(a.out, i, start + datetime.timedelta(minutes=47 * i))
        print(p)


if __name__ == "__main__":
    main()
