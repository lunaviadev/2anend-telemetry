#!/usr/bin/env python3
"""2ANEND telemetry analysis.

Reads the JSONL session logs the game writes (from this PC's log folder, any folders or
files you point it at, or pulled down from Supabase) and builds report/index.html: charts
for performance, timing, difficulty, movement and progression, a table per session, and a
list of findings worth acting on. Also exports CSVs for spreadsheets.

    python analyse.py                         this PC's 2ANEND logs
    python analyse.py logs/ more.jsonl        folders and / or files
    python analyse.py --supabase              pull every session from Supabase first
                                              (SUPABASE_URL and SUPABASE_SERVICE_KEY env vars)
    python analyse.py --no-editor             leave out sessions played in the editor
    python analyse.py --build 0.1.0+3fa9c21   one build only
    python analyse.py --out report            where the report goes (default: report/)
"""
import argparse
import collections
import csv
import datetime
import html
import json
import math
import os
import statistics
import sys
import urllib.request

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402

PINK, ICE, LIME, AMBER, CORAL, INK = "#ff4d9e", "#1a8cf2", "#73d12e", "#ffb840", "#ff6b59", "#1a1f2e"
RESULT_COL = {"perfect": ICE, "good": LIME, "miss": CORAL}
plt.rcParams.update({
    "figure.dpi": 110, "savefig.bbox": "tight", "font.size": 10, "figure.constrained_layout.use": True,
    "axes.spines.top": False, "axes.spines.right": False, "axes.titleweight": "bold",
    "axes.prop_cycle": matplotlib.cycler(color=[ICE, PINK, LIME, AMBER, "#8a6bff", CORAL]),
})


# ── loading ──────────────────────────────────────────────────────────────────
def default_log_dir():
    """Where Unity's persistentDataPath puts the game's logs on this OS."""
    company, product = "DefaultCompany", "2ANENDUNITY"
    home = os.path.expanduser("~")
    if sys.platform.startswith("win"):
        return os.path.join(home, "AppData", "LocalLow", company, product, "Telemetry")
    if sys.platform == "darwin":
        return os.path.join(home, "Library", "Application Support", company, product, "Telemetry")
    return os.path.join(home, ".config", "unity3d", company, product, "Telemetry")


def read_jsonl(paths):
    events, bad, files = [], 0, 0
    for p in paths:
        targets = []
        if os.path.isdir(p):
            for root, _, names in os.walk(p):
                targets += [os.path.join(root, n) for n in names if n.endswith(".jsonl") and n != "outbox.jsonl"]
        elif os.path.isfile(p):
            targets.append(p)
        for f in sorted(targets):
            files += 1
            with open(f, encoding="utf-8") as fh:
                for line in fh:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        events.append(json.loads(line))
                    except json.JSONDecodeError:
                        bad += 1  # a torn last line from a crash: skip it
    # the same session can arrive twice (local file + Supabase pull): keep one copy of each event
    seen, unique = set(), []
    for e in events:
        k = (e.get("session_id"), e.get("seq"))
        if k in seen:
            continue
        seen.add(k)
        unique.append(e)
    return unique, files, bad


def fetch_supabase(out_dir):
    url, key = os.environ.get("SUPABASE_URL"), os.environ.get("SUPABASE_SERVICE_KEY")
    if not url or not key:
        sys.exit("--supabase needs SUPABASE_URL and SUPABASE_SERVICE_KEY set (the service_role key: the anon key can't read)")
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, "supabase.jsonl")
    page, start, total = 1000, 0, 0
    with open(path, "w", encoding="utf-8") as out:
        while True:
            req = urllib.request.Request(
                url.rstrip("/") + "/rest/v1/telemetry_events?select=session_id,seq,type,t,utc,scene,build,data&order=session_id,seq",
                headers={"apikey": key, "Authorization": "Bearer " + key, "Range-Unit": "items", "Range": "%d-%d" % (start, start + page - 1)})
            with urllib.request.urlopen(req, timeout=60) as r:
                rows = json.loads(r.read().decode("utf-8"))
            for row in rows:
                out.write(json.dumps(row) + "\n")
            total += len(rows)
            if len(rows) < page:
                break
            start += page
    print("pulled %d events from Supabase -> %s" % (total, path))
    return path


class Session:
    def __init__(self, sid):
        self.id = sid
        self.events = []
        self.header = {}
        self.end = {}

    @property
    def code(self):
        return self.header.get("session_code", self.id[:6])

    @property
    def build(self):
        return self.header.get("build") or (self.events[0].get("build") if self.events else "?")

    @property
    def duration(self):
        if self.end.get("duration_s"):
            return float(self.end["duration_s"])
        return max((e.get("t", 0) for e in self.events), default=0.0)

    def of(self, *types):
        return [e for e in self.events if e.get("type") in types]


def group(events):
    sessions = collections.OrderedDict()
    for e in sorted(events, key=lambda e: (e.get("utc") or "", e.get("seq") or 0)):
        s = sessions.setdefault(e.get("session_id"), Session(e.get("session_id")))
        s.events.append(e)
    for s in sessions.values():
        s.events.sort(key=lambda e: e.get("seq") or 0)
        for e in s.events:
            if e["type"] == "session_start":
                s.header = e.get("data") or {}
            elif e["type"] == "session_end":
                s.end = e.get("data") or {}
    return list(sessions.values())


def drop_background_frames(sessions):
    """Perf windows and hitches from while the game window was in the background aren't play:
    Unity pauses unfocused, so the first frame back looks like one huge hitch. Drop the perf
    windows that overlap a time out of focus, and hitches within a second of coming back."""
    for s in sessions:
        away, start = [], None
        for e in s.events:
            if e.get("type") == "focus":
                on = (e.get("data") or {}).get("on")
                if on is False and start is None:
                    start = e["t"]
                elif on is True and start is not None:
                    away.append((start, e["t"]))
                    start = None
            elif e.get("type") == "app_pause" and start is None:
                start = e["t"]
        if start is not None:
            away.append((start, float("inf")))
        if not away:
            continue
        def keep(e):
            if e.get("type") == "perf":
                return not any(a < e["t"] and e["t"] - 5.5 < b for a, b in away)
            if e.get("type") == "hitch":
                return not any(a <= e["t"] <= b + 1.0 for a, b in away)
            return True
        s.events = [e for e in s.events if keep(e)]


def d(e, k, default=None):
    v = (e.get("data") or {}).get(k, default)
    return default if v is None else v


def wmean(values, weights):
    tw = sum(weights)
    return sum(v * w for v, w in zip(values, weights)) / tw if tw else float("nan")


# ── charts ───────────────────────────────────────────────────────────────────
class Report:
    def __init__(self, out):
        self.out = out
        self.sections = []
        self.findings = []
        os.makedirs(out, exist_ok=True)

    def fig(self, name, fig, title, caption=""):
        fn = name + ".png"
        fig.savefig(os.path.join(self.out, fn))
        plt.close(fig)
        self.sections.append(("img", title, fn, caption))

    def table(self, title, header, rows, caption=""):
        self.sections.append(("table", title, (header, rows), caption))

    def find(self, text):
        self.findings.append(text)


def perf_charts(rep, sessions):
    perf_sessions = [s for s in sessions if s.of("perf")]
    if not perf_sessions:
        return
    shown = perf_sessions[-6:]
    fig, axes = plt.subplots(len(shown), 1, figsize=(11, 2.4 * len(shown)), squeeze=False)
    for ax, s in zip(axes[:, 0], shown):
        perf = s.of("perf")
        t = [e["t"] / 60 for e in perf]
        ax.plot(t, [d(e, "fps", 0) for e in perf], color=ICE, lw=1.6, label="average fps")
        ax.plot(t, [d(e, "low1_fps", 0) for e in perf], color=PINK, lw=1.2, ls="--", label="1% low")
        hitches = s.of("hitch")
        if hitches:
            ax.scatter([e["t"] / 60 for e in hitches], [2] * len(hitches), marker="|", s=120, color=CORAL, label="hitch (>50 ms)", zorder=3)
        for e in s.of("scene"):
            ax.axvline(e["t"] / 60, color="#999", lw=0.6, ls=":")
            ax.text(e["t"] / 60, ax.get_ylim()[1] if ax.get_ylim()[1] > 1 else 60, " " + str(d(e, "to", "")), fontsize=7, color="#666", va="top", rotation=90)
        ax.set_ylim(bottom=0)
        ax.set_ylabel("fps")
        ax.set_title("session %s  ·  %s  ·  %s" % (s.code, s.build, s.header.get("gpu", "")), fontsize=9, loc="left")
    axes[-1, 0].set_xlabel("minutes into the session")
    axes[0, 0].legend(loc="lower right", fontsize=8, ncol=3, frameon=False)
    rep.fig("perf_timeline", fig, "Frame rate over each session",
            "Average FPS and the 1% low (the frame rate of the slowest 1% of frames, what stutter feels like) per 5 s window. Red ticks are single frames over 50 ms; dotted lines are scene changes.")

    # by build: what each version delivers, frame-weighted
    by_build = collections.defaultdict(list)
    for s in perf_sessions:
        by_build[s.build] += s.of("perf")
    builds = sorted(by_build)
    avg = [wmean([d(e, "fps", 0) for e in by_build[b]], [d(e, "frames", 1) for e in by_build[b]]) for b in builds]
    low = [statistics.median([d(e, "low1_fps", 0) for e in by_build[b]]) for b in builds]
    fig, ax = plt.subplots(figsize=(max(5, 1.6 * len(builds) + 2), 3.4))
    x = np.arange(len(builds))
    ax.bar(x - 0.2, avg, 0.4, label="average fps", color=ICE)
    ax.bar(x + 0.2, low, 0.4, label="median 1% low", color=PINK)
    for i, (a, l) in enumerate(zip(avg, low)):
        ax.text(i - 0.2, a, "%.0f" % a, ha="center", va="bottom", fontsize=8)
        ax.text(i + 0.2, l, "%.0f" % l, ha="center", va="bottom", fontsize=8)
    ax.set_xticks(x, builds, rotation=20, ha="right", fontsize=8)
    ax.set_ylabel("fps")
    ax.legend(frameon=False, fontsize=8)
    rep.fig("fps_by_build", fig, "Performance by build", "Frame-weighted average FPS and the median 1% low across every session of each build: a regression shows up as a drop between builds.")

    # by scene: where it's slow, and whether CPU or GPU is the problem
    by_scene = collections.defaultdict(list)
    for s in perf_sessions:
        for e in s.of("perf"):
            by_scene[e.get("scene") or "?"].append(e)
    scenes = sorted(by_scene, key=lambda k: -len(by_scene[k]))[:10]
    if scenes:
        fig, (a1, a2) = plt.subplots(1, 2, figsize=(11, 3.4))
        lows = [statistics.median([d(e, "low1_fps", 0) for e in by_scene[k]]) for k in scenes]
        a1.barh(scenes, lows, color=PINK)
        a1.set_xlabel("median 1% low fps")
        a1.invert_yaxis()
        # the CPU's real work is its busier thread; cpu_ms also counts waiting for vsync / the cap
        # medians: the window a scene loads in is all hitch, and would drag a mean up
        cpu = [statistics.median([max(d(e, "main_ms", 0), d(e, "render_ms", 0)) or d(e, "cpu_ms", 0) for e in by_scene[k]]) for k in scenes]
        gpu = [statistics.median([d(e, "gpu_ms", 0) for e in by_scene[k]]) for k in scenes]
        y = np.arange(len(scenes))
        a2.barh(y - 0.2, cpu, 0.4, label="CPU ms (busiest thread)", color=AMBER)
        a2.barh(y + 0.2, gpu, 0.4, label="GPU ms", color=ICE)
        a2.set_yticks(y, scenes)
        a2.invert_yaxis()
        a2.axvline(16.7, color="#999", lw=0.8, ls=":")
        a2.set_xlabel("typical frame cost (median ms, 16.7 = 60 fps)")
        a2.legend(frameon=False, fontsize=8)
        rep.fig("perf_by_scene", fig, "Where it's slow, and why",
                "Left: the worst-feeling scenes. Right: CPU against GPU time per frame. Whichever is bigger is the one to optimise (GPU time is 0 where the platform doesn't report it).")
        worst = min(zip(lows, scenes))
        rep.find("The roughest scene is <b>%s</b>, with a median 1%% low of %.0f fps." % (html.escape(worst[1]), worst[0]))
        both = [(c, g, k) for c, g, k in zip(cpu, gpu, scenes) if g > 0]
        if both:
            c, g, k = max(both, key=lambda x: max(x[0], x[1]))
            rep.find("The most expensive scene, <b>%s</b>, is %s-bound (CPU %.1f ms against GPU %.1f ms)." % (html.escape(k), "GPU" if g > c else "CPU", c, g))

    # memory
    fig, ax = plt.subplots(figsize=(11, 3))
    for s in shown:
        perf = s.of("perf")
        ax.plot([e["t"] / 60 for e in perf], [d(e, "mem_alloc_mb", 0) for e in perf], lw=1.3, label=s.code)
    ax.set_xlabel("minutes into the session")
    ax.set_ylabel("allocated MB")
    ax.legend(frameon=False, fontsize=8, ncol=6)
    rep.fig("memory", fig, "Memory over time", "Allocated memory per session. A line that keeps climbing and never comes back down is a leak.")

    # hitches: what was going on
    hitches = [e for s in sessions for e in s.of("hitch")]
    if hitches:
        cause = collections.Counter()
        for e in hitches:
            if d(e, "loading"):
                cause["during a load"] += 1
            elif d(e, "gc_ran"):
                cause["during garbage collection"] += 1
            elif (d(e, "ctx") or {}).get("song"):
                cause["in a battle (%s)" % d(e, "ctx")["song"]] += 1
            else:
                cause["in " + str(e.get("scene") or "?")] += 1
        top = cause.most_common(8)
        fig, ax = plt.subplots(figsize=(8, 0.4 * len(top) + 1.2))
        ax.barh([k for k, _ in top], [v for _, v in top], color=CORAL)
        ax.invert_yaxis()
        ax.set_xlabel("hitches")
        rep.fig("hitches", fig, "What hitches happen during", "Every frame over 50 ms, grouped by what was going on at that moment (logged with the hitch).")
        k, v = top[0]
        rep.find("%d hitches in total; most of them happen <b>%s</b> (%d)." % (len(hitches), html.escape(k), v))

    loads = [e for s in sessions for e in s.of("span") if d(e, "name") == "load"]
    if loads:
        by = collections.defaultdict(list)
        for e in loads:
            by[str(d(e, "to") or d(e, "scene") or "?")].append(d(e, "ms", 0) / 1000)
        keys = sorted(by, key=lambda k: -statistics.median(by[k]))
        fig, ax = plt.subplots(figsize=(8, 0.45 * len(keys) + 1.2))
        data = [by[k] for k in keys]
        style = dict(patch_artist=True, boxprops=dict(facecolor="#cfe6ff"))
        try:  # matplotlib 3.10+
            ax.boxplot(data, orientation="horizontal", tick_labels=keys, **style)
        except TypeError:
            ax.boxplot(data, vert=False, labels=keys, **style)
        ax.invert_yaxis()
        ax.set_xlabel("seconds to first frame")
        rep.fig("loads", fig, "Load times", "From a scene change starting to the new scene's first frame.")
        k = keys[0]
        rep.find("The slowest load is into <b>%s</b>, with a median of %.1f s." % (html.escape(k), statistics.median(by[k])))


def timing_charts(rep, sessions):
    judges = [e for s in sessions for e in s.of("judge")]
    if not judges:
        return
    hits = [d(e, "ms") for e in judges if d(e, "ms") is not None]
    if hits:
        fig, ax = plt.subplots(figsize=(9, 3.4))
        ax.hist(hits, bins=np.arange(-125, 126, 5), color=ICE, alpha=0.9)
        for x, c in ((-50, ICE), (50, ICE), (-120, LIME), (120, LIME)):
            ax.axvline(x, color=c, ls=":", lw=1)
        mean, med, sd = statistics.mean(hits), statistics.median(hits), statistics.pstdev(hits)
        ax.axvline(mean, color=PINK, lw=2)
        ax.text(mean, ax.get_ylim()[1] * 0.95, "  mean %+.1f ms" % mean, color=PINK, fontsize=9, va="top")
        ax.set_xlabel("ms early (-) / late (+)")
        ax.set_ylabel("notes")
        rep.fig("timing", fig, "Hit timing", "Every note that was hit, by how early or late. Dotted lines are the PERFECT (±50 ms) and GOOD (±120 ms) windows. A peak off zero means the audio offset is wrong for players, not that they're bad at the game.")
        if abs(mean) >= 8:
            rep.find("Players hit <b>%.0f ms %s</b> on average (median %+.0f, spread %.0f ms). Shifting the default audio offset by %+.0f ms would centre them." % (abs(mean), "late" if mean > 0 else "early", med, sd, -mean))
        else:
            rep.find("Hit timing is centred (mean %+.1f ms, spread %.0f ms): the default audio offset is right." % (mean, sd))

        # per player: who needs calibrating
        per = collections.defaultdict(list)
        for s in sessions:
            for e in s.of("judge"):
                if d(e, "ms") is not None:
                    per[s.header.get("player_id", "?")].append(d(e, "ms"))
        rows = []
        for pid, ms in sorted(per.items(), key=lambda kv: -len(kv[1])):
            rows.append([pid, len(ms), "%+.1f" % statistics.mean(ms), "%.1f" % statistics.pstdev(ms)])
        rep.table("Timing by player", ["player", "notes hit", "mean ms", "spread ms"], rows, "Each player's own offset: a big mean means that player's setup (headphones, Bluetooth, TV) needs calibrating.")

    # lanes
    lanes = collections.defaultdict(collections.Counter)
    lane_ms = collections.defaultdict(list)
    for e in judges:
        lanes[d(e, "lane", 0)][d(e, "result", "?")] += 1
        if d(e, "ms") is not None:
            lane_ms[d(e, "lane", 0)].append(d(e, "ms"))
    keys = sorted(lanes)
    names = {0: "D", 1: "F", 2: "J", 3: "K"}
    fig, ax = plt.subplots(figsize=(7, 3.2))
    bottom = np.zeros(len(keys))
    for r in ("perfect", "good", "miss"):
        vals = np.array([lanes[k][r] / max(1, sum(lanes[k].values())) * 100 for k in keys])
        ax.bar([names.get(k, str(k)) for k in keys], vals, bottom=bottom, color=RESULT_COL[r], label=r)
        bottom += vals
    for i, k in enumerate(keys):
        if lane_ms[k]:
            ax.text(i, 101, "%+.0f ms" % statistics.mean(lane_ms[k]), ha="center", fontsize=8)
    ax.set_ylabel("% of notes")
    ax.set_ylim(0, 110)
    ax.legend(frameon=False, fontsize=8, ncol=3, loc="lower center")
    rep.fig("lanes", fig, "Accuracy by lane", "How each key does, with its average timing above. A weak lane usually means a hand-position problem in the charting.")
    miss_rate = {k: lanes[k]["miss"] / max(1, sum(lanes[k].values())) for k in keys}
    worst = max(miss_rate, key=miss_rate.get)
    rep.find("Lane <b>%s</b> is missed the most (%.0f%% of its notes)." % (names.get(worst, worst), miss_rate[worst] * 100))


def difficulty_charts(rep, sessions):
    judges = [e for s in sessions for e in s.of("judge") if d(e, "song")]
    by_song = collections.defaultdict(list)
    for e in judges:
        by_song[(d(e, "song"), d(e, "chart"))].append(e)
    songs = sorted((k for k in by_song if len(by_song[k]) >= 20), key=lambda k: -len(by_song[k]))[:8]
    hardest = []
    for song, chart in songs:
        js = by_song[(song, chart)]
        end = max(d(e, "song_t", 0) for e in js)
        bins = np.arange(0, end + 2, 2.0)
        counts = {r: np.histogram([d(e, "song_t", 0) for e in js if d(e, "result") == r], bins)[0] for r in ("perfect", "good", "miss")}
        total = sum(counts.values())
        rate = np.divide(counts["miss"], total, out=np.zeros(len(total)), where=total > 0) * 100
        fig, ax = plt.subplots(figsize=(11, 2.8))
        bottom = np.zeros(len(bins) - 1)
        for r in ("perfect", "good", "miss"):
            ax.bar(bins[:-1], counts[r], 2.0, bottom=bottom, align="edge", color=RESULT_COL[r], label=r, alpha=0.85)
            bottom += counts[r]
        ax2 = ax.twinx()
        ax2.plot(bins[:-1] + 1, rate, color=INK, lw=1.4)
        ax2.set_ylabel("miss %")
        ax2.set_ylim(0, 100)
        ax2.spines["right"].set_visible(True)
        ax.set_xlabel("song time (s)")
        ax.set_ylabel("notes")
        ax.legend(frameon=False, fontsize=8, ncol=3, loc="upper left")
        plays = len({e.get("session_id") for e in js})
        rep.fig("missmap_%s_%s" % (song, chart), fig, "Miss map: %s (%s)" % (song, chart),
                "Every note of this chart across %d session(s), in 2 s slices of the song; the line is the miss rate. Spikes are the bars to look at in Chart Studio." % plays)
        enough = total >= max(5, total.max() * 0.2)
        if enough.any():
            i = int(np.argmax(np.where(enough, rate, -1)))
            hardest.append((rate[i], song, chart, bins[i]))
    for r, song, chart, t0 in [h for h in sorted(hardest, reverse=True) if h[0] > 0][:3]:
        rep.find("<b>%s (%s)</b> %.0f–%.0f s: %.0f%% of notes missed, the hardest stretch of that chart." % (html.escape(str(song)), html.escape(str(chart)), t0, t0 + 2, r))

    battles = [e for s in sessions for e in s.of("battle_end")]
    if battles:
        by = collections.defaultdict(list)
        for e in battles:
            by[(d(e, "song"), d(e, "chart"))].append(e)
        starts = collections.defaultdict(list)
        for s in sessions:
            for e in s.of("battle_start"):
                starts[(d(e, "song"), d(e, "chart"))].append(d(e, "attempt", 1))
        rows = []
        for k, es in sorted(by.items(), key=lambda kv: -len(kv[1])):
            wins = sum(1 for e in es if d(e, "outcome") == "Win")
            rows.append([k[0], k[1], len(es), "%.0f%%" % (wins / len(es) * 100),
                         "%.0f" % statistics.mean(d(e, "score", 0) for e in es),
                         "%.1f%%" % statistics.mean(d(e, "accuracy", 0) for e in es),
                         max(starts.get(k, [1])),
                         "%.0f s" % statistics.median(d(e, "duration_s", 0) for e in es)])
        rep.table("Battles", ["song", "chart", "played", "won", "avg score", "avg accuracy", "most attempts", "median length"], rows,
                  "Every battle finished. A low win rate with lots of attempts is a difficulty wall.")
        lost = [(k, es) for k, es in by.items() if len(es) >= 3]
        if lost:
            k, es = min(lost, key=lambda kv: sum(1 for e in kv[1] if d(e, "outcome") == "Win") / len(kv[1]))
            wr = sum(1 for e in es if d(e, "outcome") == "Win") / len(es)
            if wr < 0.6:
                deaths = [d(e, "song_t", 0) for e in es if d(e, "outcome") == "Lose"]
                rep.find("<b>%s (%s)</b> is won only %.0f%% of the time%s." % (
                    html.escape(str(k[0])), html.escape(str(k[1])), wr * 100,
                    ("; players usually fall around %.0f s in" % statistics.median(deaths)) if deaths else ""))


def world_charts(rep, sessions):
    pts = collections.defaultdict(list)
    for s in sessions:
        for e in s.of("path"):
            for p in d(e, "pts", []):
                if len(p) >= 4:
                    pts[d(e, "ch", "?")].append((p[1], p[3]))
    for ch, xs in pts.items():
        if len(xs) < 20:
            continue
        a = np.array(xs)
        fig, ax = plt.subplots(figsize=(7, 6))
        hb = ax.hexbin(a[:, 0], a[:, 1], gridsize=40, cmap="magma_r", mincnt=1, bins="log")
        fig.colorbar(hb, ax=ax, label="seconds spent (log)")
        ax.set_aspect("equal")
        ax.set_xlabel("x")
        ax.set_ylabel("z")
        rep.fig("heatmap_" + ch, fig, "Where players go: " + ch,
                "One sample a second of the player's position, across %d session(s). Dark cells are where people spend their time; empty areas nobody found." % len({s.id for s in sessions if any(d(e, 'ch') == ch for e in s.of('path'))}))

    # progression: how many sessions reach each story flag
    first = collections.defaultdict(dict)
    for s in sessions:
        for e in s.of("flag"):
            if d(e, "on", True):
                first[d(e, "flag")].setdefault(s.id, e["t"])
    if first:
        n = len(sessions)
        order = sorted(first, key=lambda f: statistics.median(first[f].values()))[:25]
        fig, ax = plt.subplots(figsize=(9, 0.32 * len(order) + 1.2))
        counts = [len(first[f]) for f in order]
        ax.barh(order, counts, color=ICE)
        for i, c in enumerate(counts):
            ax.text(c, i, " %d%%" % (c / n * 100), va="center", fontsize=8)
        ax.invert_yaxis()
        ax.set_xlabel("sessions that got here (of %d)" % n)
        rep.fig("funnel", fig, "Story progress", "Story flags in the order players reach them, and how many sessions got that far. The big drop is where people stop.")
        drops = [(counts[i] - counts[i + 1], order[i], order[i + 1]) for i in range(len(order) - 1)]
        if drops:
            dr, a, b = max(drops)
            if dr > 0:
                rep.find("The biggest drop-off in the story is between <b>%s</b> and <b>%s</b> (%d session(s) stop there)." % (html.escape(str(a)), html.escape(str(b)), dr))

    # objectives: how long each one takes (where people get stuck)
    spent = collections.defaultdict(list)
    for s in sessions:
        obj = s.of("objective")
        for a, b in zip(obj, obj[1:]):
            spent[d(a, "text", "?")].append((b["t"] - a["t"]) / 60)
    if spent:
        keys = sorted(spent, key=lambda k: -statistics.median(spent[k]))[:12]
        fig, ax = plt.subplots(figsize=(9, 0.42 * len(keys) + 1.2))
        ax.barh([k[:48] for k in keys], [statistics.median(spent[k]) for k in keys], color=AMBER)
        ax.invert_yaxis()
        ax.set_xlabel("median minutes on this objective")
        rep.fig("objectives", fig, "Time per objective", "How long players spend on each objective before the next one. A long bar is a confusing objective or a hard-to-find target.")
        k = keys[0]
        rep.find("The objective players spend longest on is <b>%s</b> (median %.1f min)." % (html.escape(k), statistics.median(spent[k])))

    talks = collections.defaultdict(list)
    for s in sessions:
        for e in s.of("talk_end"):
            talks[d(e, "npc", "?")].append(d(e, "secs", 0))
    if talks:
        rows = [[k, len(v), "%.1f s" % statistics.median(v)] for k, v in sorted(talks.items(), key=lambda kv: -len(kv[1]))[:15]]
        rep.table("Conversations", ["npc", "talks", "median length"], rows, "Very short conversations with talkative characters usually mean people are skipping the dialogue.")


def session_table(rep, sessions):
    rows = []
    for s in sessions:
        perf = s.of("perf")
        fps = wmean([d(e, "fps", 0) for e in perf], [d(e, "frames", 1) for e in perf]) if perf else float("nan")
        low = statistics.median([d(e, "low1_fps", 0) for e in perf]) if perf else float("nan")
        battles = s.of("battle_end")
        wins = sum(1 for e in battles if d(e, "outcome") == "Win")
        start = s.events[0].get("utc", "")[:16].replace("T", " ") if s.events else ""
        rows.append([s.code, s.header.get("player_id", "?"), s.build, start, "%.1f" % (s.duration / 60),
                     s.header.get("platform", "?") + (" (editor)" if s.header.get("editor") else ""),
                     s.header.get("gpu", "?"), "%.0f" % fps, "%.0f" % low, len(s.of("hitch")),
                     "%d / %d" % (wins, len(battles)), len(s.events)])
    rep.table("Sessions", ["code", "player", "build", "started (UTC)", "minutes", "platform", "GPU", "avg fps", "1% low", "hitches", "battles won", "events"],
              rows, "Session codes match the ones testers write in the survey, so each row can be joined to that tester's answers.")


# ── csv ──────────────────────────────────────────────────────────────────────
def export_csv(out, sessions):
    def write(name, header, rows):
        with open(os.path.join(out, name), "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(header)
            w.writerows(rows)

    allev = [e for s in sessions for e in s.events]
    write("events.csv", ["session_id", "seq", "type", "t", "utc", "scene", "build", "data"],
          [[e.get("session_id"), e.get("seq"), e.get("type"), e.get("t"), e.get("utc"), e.get("scene"), e.get("build"), json.dumps(e.get("data"))] for e in allev])
    write("judgements.csv", ["session_id", "song", "chart", "lane", "note", "result", "ms", "song_t"],
          [[e["session_id"], d(e, "song"), d(e, "chart"), d(e, "lane"), d(e, "note"), d(e, "result"), d(e, "ms"), d(e, "song_t")] for e in allev if e["type"] == "judge"])
    keys = ["fps", "low1_fps", "low01_fps", "p50_ms", "p95_ms", "p99_ms", "max_ms", "cpu_ms", "gpu_ms", "mem_alloc_mb", "gc_collections", "hitches"]
    write("perf.csv", ["session_id", "build", "scene", "t"] + keys,
          [[e["session_id"], e.get("build"), e.get("scene"), e.get("t")] + [d(e, k) for k in keys] for e in allev if e["type"] == "perf"])
    keys = ["song", "chart", "outcome", "score", "max_combo", "accuracy", "grade", "perfect", "good", "miss", "duration_s", "song_t"]
    write("battles.csv", ["session_id", "build"] + keys,
          [[e["session_id"], e.get("build")] + [d(e, k) for k in keys] for e in allev if e["type"] == "battle_end"])


# ── html ─────────────────────────────────────────────────────────────────────
def write_html(rep, sessions, sources, sample):
    def table_html(header, rows):
        h = "".join("<th>%s</th>" % html.escape(str(x)) for x in header)
        b = "".join("<tr>%s</tr>" % "".join("<td>%s</td>" % html.escape(str(c)) for c in r) for r in rows)
        return "<div class='scroll'><table><thead><tr>%s</tr></thead><tbody>%s</tbody></table></div>" % (h, b)

    parts = []
    for kind, title, payload, caption in rep.sections:
        body = "<img src='%s' alt='%s'>" % (payload, html.escape(title)) if kind == "img" else table_html(*payload)
        parts.append("<section><h2>%s</h2><p class='cap'>%s</p>%s</section>" % (html.escape(title), caption, body))
    n_ev = sum(len(s.events) for s in sessions)
    players = len({s.header.get("player_id") for s in sessions})
    builds = sorted({s.build for s in sessions})
    banner = "<div class='banner'>SAMPLE DATA: generated by make_sample_data.py, not a real playtest.</div>" if sample else ""
    findings = "".join("<li>%s</li>" % f for f in rep.findings) or "<li>Not enough data yet.</li>"
    doc = """<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>2ANEND playtest report</title><style>
:root{--ink:#1a1f2e;--sub:#5c6680;--line:#dde3ef;--bg:#f4f7fc;--card:#fff;--pink:#ff4d9e;--ice:#1a8cf2}
body{margin:0;font:15px/1.5 system-ui,Segoe UI,sans-serif;color:var(--ink);background:var(--bg)}
header{background:linear-gradient(120deg,#0f1530,#1d2b5c);color:#fff;padding:32px max(16px,calc(50%% - 520px))}
header h1{margin:0;font-size:28px;letter-spacing:.02em}header p{margin:6px 0 0;color:#b9c6e8}
main{max-width:1040px;margin:0 auto;padding:16px}
section,.find{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:18px 20px;margin:16px 0}
h2{margin:0 0 4px;font-size:18px}.cap{margin:0 0 12px;color:var(--sub);font-size:14px}
img{max-width:100%%;height:auto;display:block}.find ul{margin:8px 0 0;padding-left:20px}.find li{margin:6px 0}
.stats{display:flex;gap:12px;flex-wrap:wrap;margin-top:14px}.stat{background:rgba(255,255,255,.08);border-radius:10px;padding:8px 14px}
.stat b{display:block;font-size:22px}.scroll{overflow-x:auto}table{border-collapse:collapse;width:100%%;font-size:13px}
th,td{text-align:left;padding:6px 10px;border-bottom:1px solid var(--line);white-space:nowrap}th{color:var(--sub);font-weight:600}
.banner{background:var(--pink);color:#fff;font-weight:700;text-align:center;padding:10px}
footer{color:var(--sub);font-size:12px;text-align:center;padding:24px}
</style></head><body>%s<header><h1>2ANEND playtest report</h1>
<p>Generated %s from %s</p><div class="stats">
<div class="stat"><b>%d</b>sessions</div><div class="stat"><b>%d</b>players</div><div class="stat"><b>%s</b>events</div>
<div class="stat"><b>%.1f h</b>played</div><div class="stat"><b>%d</b>build(s)</div></div></header>
<main><div class="find"><h2>Findings</h2><ul>%s</ul></div>%s</main>
<footer>CSV exports: events.csv · judgements.csv · perf.csv · battles.csv</footer></body></html>""" % (
        banner, datetime.datetime.now().strftime("%Y-%m-%d %H:%M"), html.escape(", ".join(sources)),
        len(sessions), players, "{:,}".format(n_ev), sum(s.duration for s in sessions) / 3600, len(builds), findings, "".join(parts))
    path = os.path.join(rep.out, "index.html")
    with open(path, "w", encoding="utf-8") as f:
        f.write(doc)
    return path


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="*", help="log folders or .jsonl files (default: this PC's 2ANEND log folder)")
    ap.add_argument("--out", default="report")
    ap.add_argument("--supabase", action="store_true", help="pull events from Supabase first")
    ap.add_argument("--no-editor", action="store_true", help="leave out sessions played in the editor")
    ap.add_argument("--build", help="only this build")
    a = ap.parse_args()

    paths = list(a.paths)
    if a.supabase:
        paths.append(fetch_supabase(os.path.join(a.out, "data")))
    if not paths:
        paths = [default_log_dir()]
    events, files, bad = read_jsonl(paths)
    sessions = group(events)
    if a.no_editor:
        sessions = [s for s in sessions if not s.header.get("editor")]
    if a.build:
        sessions = [s for s in sessions if s.build == a.build]
    if not sessions:
        sys.exit("no sessions found in: " + ", ".join(paths))
    drop_background_frames(sessions)
    print("%d events in %d session(s) from %d file(s)%s" % (len(events), len(sessions), files, (", %d torn line(s) skipped" % bad) if bad else ""))

    rep = Report(a.out)
    session_table(rep, sessions)
    perf_charts(rep, sessions)
    timing_charts(rep, sessions)
    difficulty_charts(rep, sessions)
    world_charts(rep, sessions)
    export_csv(a.out, sessions)
    sample = any("synthetic" in str(s.header.get("study", "")).lower() for s in sessions)
    path = write_html(rep, sessions, paths, sample)
    print("report -> " + os.path.abspath(path))


if __name__ == "__main__":
    main()
