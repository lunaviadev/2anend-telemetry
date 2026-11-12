-- 2ANEND telemetry: one table of events, plus views for analysis.
-- Run in the Supabase dashboard: SQL Editor > New query > paste > Run.
--
-- The game uploads with the project's public "anon" key. Row level security lets that key
-- INSERT and nothing else, so a key pulled out of a build can't read, change or delete
-- anybody's data. Analysis reads with the service_role key, which never goes in a build.

create table if not exists public.telemetry_events (
    id          bigint generated always as identity primary key,
    session_id  text             not null,
    seq         integer          not null,
    type        text             not null,
    t           double precision not null,          -- seconds since the session started
    utc         timestamptz      not null,
    scene       text,
    build       text,
    data        jsonb            not null default '{}'::jsonb,
    received_at timestamptz      not null default now(),
    -- a retried batch can't double up (the game sends on_conflict=session_id,seq)
    unique (session_id, seq)
);

create index if not exists telemetry_events_type_idx    on public.telemetry_events (type);
create index if not exists telemetry_events_session_idx on public.telemetry_events (session_id);
create index if not exists telemetry_events_build_idx   on public.telemetry_events (build);

alter table public.telemetry_events enable row level security;

-- testers' builds may add rows (sane sizes only) and do nothing else
drop policy if exists "playtest builds can insert" on public.telemetry_events;
create policy "playtest builds can insert"
    on public.telemetry_events
    for insert
    to anon
    with check (
        length(session_id) between 8 and 64
        and length(type) between 1 and 64
        and octet_length(data::text) < 65536
    );

grant insert on public.telemetry_events to anon;
revoke select, update, delete on public.telemetry_events from anon;

-- ── views (service_role / dashboard only) ────────────────────────────────────

-- one row per session: who, what, on what, how long
create or replace view public.telemetry_sessions as
select
    s.session_id,
    s.data ->> 'session_code'      as session_code,
    s.data ->> 'player_id'         as player_id,
    s.build,
    s.utc                          as started,
    s.data ->> 'platform'          as platform,
    s.data ->> 'gpu'               as gpu,
    (s.data ->> 'ram_mb')::int     as ram_mb,
    (s.data ->> 'editor')::boolean as editor,
    e.utc                          as ended,
    (e.data ->> 'duration_s')::float                  as duration_s,
    (e.data -> 'perf' ->> 'fps')::float               as avg_fps,
    (e.data -> 'perf' ->> 'low1_fps')::float          as low1_fps,
    (e.data ->> 'hitches')::int                       as hitches
from public.telemetry_events s
left join public.telemetry_events e
    on e.session_id = s.session_id and e.type = 'session_end'
where s.type = 'session_start';

-- every note judged, flattened
create or replace view public.telemetry_judgements as
select
    session_id, build, utc,
    data ->> 'song'             as song,
    data ->> 'chart'            as chart,
    (data ->> 'lane')::int      as lane,
    data ->> 'note'             as note,
    data ->> 'result'           as result,
    (data ->> 'ms')::float      as ms,
    (data ->> 'song_t')::float  as song_t
from public.telemetry_events
where type = 'judge';

-- performance windows
create or replace view public.telemetry_perf as
select
    session_id, build, scene, t,
    (data ->> 'fps')::float          as fps,
    (data ->> 'low1_fps')::float     as low1_fps,
    (data ->> 'p99_ms')::float       as p99_ms,
    (data ->> 'cpu_ms')::float       as cpu_ms,
    (data ->> 'gpu_ms')::float       as gpu_ms,
    (data ->> 'mem_alloc_mb')::float as mem_alloc_mb
from public.telemetry_events
where type = 'perf';

-- battles
create or replace view public.telemetry_battles as
select
    session_id, build, utc,
    data ->> 'song'                as song,
    data ->> 'chart'               as chart,
    data ->> 'outcome'             as outcome,
    (data ->> 'score')::int        as score,
    (data ->> 'accuracy')::float   as accuracy,
    data ->> 'grade'               as grade,
    (data ->> 'duration_s')::float as duration_s,
    (data ->> 'song_t')::float     as ended_at_song_t
from public.telemetry_events
where type = 'battle_end';

-- ── uploads ──────────────────────────────────────────────────────────────────
-- The game uploads through this function, not by inserting into the table directly. It
-- inserts a batch and skips rows it already has (same session_id + seq), so a batch that
-- was stored but whose reply got lost can be re-sent safely. It runs with the function
-- owner's rights (security definer) because skipping duplicates means checking the table,
-- which the build's key is not allowed to read. It only ever inserts, checks sizes, and
-- returns how many rows were new.
create or replace function public.ingest_telemetry(rows jsonb)
returns integer
language sql
security definer
set search_path = public
as $$
    with ins as (
        insert into public.telemetry_events (session_id, seq, type, t, utc, scene, build, data)
        select r ->> 'session_id', (r ->> 'seq')::int, r ->> 'type', (r ->> 't')::float8,
               (r ->> 'utc')::timestamptz, r ->> 'scene', r ->> 'build', coalesce(r -> 'data', '{}'::jsonb)
        from jsonb_array_elements(case when jsonb_typeof(rows) = 'array' and jsonb_array_length(rows) <= 1000 then rows else '[]'::jsonb end) r
        where length(r ->> 'session_id') between 8 and 64
          and length(r ->> 'type') between 1 and 64
          and octet_length(coalesce(r -> 'data', '{}'::jsonb)::text) < 65536
        on conflict (session_id, seq) do nothing
        returning 1
    )
    select count(*)::int from ins;
$$;

revoke all on function public.ingest_telemetry(jsonb) from public;
grant execute on function public.ingest_telemetry(jsonb) to anon;

-- ── keep the read side private ──────────────────────────────────────────────
-- Projects that "automatically expose new tables" grant the API roles access to new tables
-- AND views, and a view reads past row level security by default. So: views run with the
-- caller's rights (security_invoker), and the public API roles get no access to them, and
-- nothing but insert on the table.
revoke select, update, delete, truncate on public.telemetry_events from anon, authenticated;
alter view public.telemetry_sessions   set (security_invoker = true);
alter view public.telemetry_judgements set (security_invoker = true);
alter view public.telemetry_perf       set (security_invoker = true);
alter view public.telemetry_battles    set (security_invoker = true);
revoke all on public.telemetry_sessions, public.telemetry_judgements, public.telemetry_perf, public.telemetry_battles from anon, authenticated;

-- a tester asks for their data to be removed: they give their session code
-- (run as service_role):
--   delete from public.telemetry_events
--   where session_id in (select session_id from public.telemetry_sessions where session_code = '7F3K-Q2');
