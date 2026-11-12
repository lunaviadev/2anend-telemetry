# Technical Commentary: QA & Telemetry for 2ANEND

## Collecting feedback and consent

For this project my teammate and I split the testing system into two halves. She built the user testing survey, which covers consent, the build and platform tested, ratings for difficulty and enjoyment, and written feedback on bugs and suggestions. I built the telemetry system, which records what actually happens while someone plays.

I made consent the first thing the telemetry checks. When the game launches, a prompt explains in plain language what will be recorded and gives the tester a short session code. Until they answer, events only wait in memory. If they say no, those events are thrown away and nothing is written. If they agree, everything is saved in order. A tester can change their mind at any time from the F3 panel, which deletes everything stored on that PC. I deliberately avoided collecting personal data: there are no names, accounts or device names, only a random ID for the install. Testers type their session code into the survey, which lets us match each person's answers to their own play data.

## What I logged

I split the telemetry into three areas:

- **Performance:** every frame is sampled. Every five seconds the game logs average FPS, the 1% and 0.1% lows, frame time percentiles, CPU and GPU time, memory and garbage collection. Any frame over 50 ms is logged as a hitch, along with what was happening at the time, such as a load, garbage collection, or which song and how far into it. Scene loads are timed until the first frame appears.
- **Gameplay:** because 2ANEND is a rhythm game, I logged every note judgement with its timing error in milliseconds, as well as hold releases, battle results, retries, pauses and forfeits.
- **Exploration:** the player's position once a second, conversations, objective changes and story progress.

I also added a benchmark launch option so different builds can be compared fairly.

## Tools and frameworks

I built the system as its own Unity package in C# so it doesn't depend on the game. One bridge script connects it to 2ANEND, which is also why I can submit it without the whole project. For performance data I used Unity's `FrameTimingManager` and `ProfilerRecorder`. Events are saved as JSON lines, which survive crashes. Sessions upload to a Supabase (PostgreSQL) database through an offline queue that keeps retrying until the server confirms. I set up row level security so the key inside the game can only add data, never read it. Finally, I wrote a Python script using matplotlib that turns the sessions into an HTML report with charts and findings.

## How the data improves the game

Each part of the report points to something specific to fix:

- The hit timing chart shows whether the default audio offset is wrong.
- The miss maps show exactly which seconds of a chart are too hard.
- Lane accuracy highlights awkward finger patterns.
- Win rates and retries reveal difficulty spikes.
- The story funnel and time per objective show where players quit or get lost.
- Performance by scene, with the CPU and GPU split and the hitch causes, tells me what to optimise.

Comparing builds then shows whether a fix actually worked.

The benchmarks have already found three things worth fixing:
- **A game-breaking build bug.** The first build crashed because shaders the game loads by name were being stripped out of builds, which would have broken notes and effects for every playtester. Fixing it meant adding them to the always-included shaders.
- **A 60 fps cap with lots of headroom.** The game uses under 5 ms of each 16.7 ms frame, but it's capped at 60 fps even on a 165 Hz display. For a rhythm game, matching the refresh rate would make notes smoother.
- **Loading stutter.** Most stutter comes from loading, and the Overworld is the slowest to load (2.3 s median).

Linking telemetry to the survey makes feedback much more useful. If a tester rates a song "too hard", I can check whether they struggled with one section or were held back by a bad audio offset. Unfortunately no playtesters could be recruited in time to test the game properly, so the only gameplay data is a session I played myself. Even that one session showed why splitting the data matters. Overall I hit notes 13 ms early, which on its own suggests changing the audio offset. The lane chart showed it was almost entirely my left hand (D and F about 21 ms early, J and K nearly spot on), so the real issue is technique or the left-hand patterns, not the offset. The miss map also showed the boss remix is fine until 22-24 seconds, where 44% of notes were missed, so that is the section I will revisit in the chart editor. With real testers I could check whether these patterns hold for other players.

## AI disclosure

I used Claude Code (Anthropic's AI coding assistant) throughout this project.

**What Claude Code did:**
- Wrote most of the code: the telemetry package, the bridge into 2ANEND, the Python analysis script and the Supabase schema.
- Debugged the problems that testing found: the build crash from stripped shaders, the refused uploads, and the benchmark freezing.
- Set up and tested the Supabase project.
- Ran the benchmarks and captured the game screenshots.
- Drafted this commentary and the README.

**What I did:**
- Decided what the system needed to do, how it fits into 2ANEND, and how it splits with my teammate's survey work, and directed and approved the approach.
- Played the playtest session and recorded the battle GIF.
- Reviewed and edited the written work.

All the data, screenshots and results shown are real and come from the game.
