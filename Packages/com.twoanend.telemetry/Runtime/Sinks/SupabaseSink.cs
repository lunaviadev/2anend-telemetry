using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace TwoAnEnd.Playtest
{
    /// Uploads events to Supabase in batches, through the ingest_telemetry database function
    /// (supabase/schema.sql), called over the REST API (PostgREST).
    ///
    /// Offline first: every event goes into an outbox that's saved to disk (outbox.jsonl), and
    /// only leaves it once the server has said 2xx. No network, a closed laptop, a crash mid
    /// upload: the events wait and go up next time, in this session or a later one. Retries
    /// back off (5 s doubling to 5 min). The function skips rows it already has (same
    /// session_id + seq), so a batch that was stored but whose reply got lost can be sent
    /// again without doubling up. (A plain upsert can't do that: skipping duplicates means
    /// reading the table, and the build's key isn't allowed to read.)
    ///
    /// Uses the project's public anon (or publishable) key: the table's row level security only lets it insert,
    /// never read, so a key pulled out of a build can't be used to see anyone's data.
    public sealed class SupabaseSink : IDisposable
    {
        private const int BatchSize = 250, MaxQueue = 50000;
        private const float MinBackoff = 5f, MaxBackoff = 300f;

        public int Queued => _queue.Count;
        public long Sent { get; private set; }
        public int Failures { get; private set; }
        public string Status { get; private set; } = "idle";
        public string LastError { get; private set; } = "";

        private readonly string _endpoint, _key, _outbox;
        private readonly List<string> _queue = new List<string>();
        private UnityWebRequest _req;
        private int _inFlight;
        private float _nextTry, _backoff;
        private bool _dirty;

        public SupabaseSink(TelemetryConfig cfg, string dir)
        {
            _endpoint = cfg.supabaseUrl.TrimEnd('/') + "/rest/v1/rpc/" + cfg.supabaseFunction;
            _key = cfg.supabaseAnonKey;
            Directory.CreateDirectory(dir);
            _outbox = Path.Combine(dir, "outbox.jsonl");
            // whatever an earlier session didn't get to send
            try
            {
                if (File.Exists(_outbox))
                    foreach (var line in File.ReadAllLines(_outbox))
                        if (!string.IsNullOrWhiteSpace(line)) _queue.Add(line);
            }
            catch (IOException e) { LastError = e.Message; }
            if (_queue.Count > 0) Status = $"resuming {_queue.Count}";
        }

        public void Enqueue(string line)
        {
            _queue.Add(line);
            if (_queue.Count > MaxQueue) _queue.RemoveRange(0, _queue.Count - MaxQueue);
            _dirty = true;
        }

        public void Tick(float now, float interval)
        {
            if (_req != null)
            {
                if (!_req.isDone) return;
                Finish(now, interval);
                return;
            }
            if (_queue.Count == 0 || now < _nextTry) return;
            if (Application.internetReachability == NetworkReachability.NotReachable)
            {
                Status = "offline";
                _nextTry = now + MinBackoff;
                return;
            }
            Send();
        }

        private void Send()
        {
            _inFlight = Mathf.Min(BatchSize, _queue.Count);
            var sb = new StringBuilder(_inFlight * 200);
            sb.Append("{\"rows\":[");
            for (int i = 0; i < _inFlight; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_queue[i]);
            }
            sb.Append("]}");
            _req = new UnityWebRequest(_endpoint, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString())),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 20,
            };
            _req.SetRequestHeader("Content-Type", "application/json");
            _req.SetRequestHeader("apikey", _key);
            // legacy anon keys are JWTs and go in Authorization too; new sb_publishable_ keys don't
            if (_key.StartsWith("eyJ")) _req.SetRequestHeader("Authorization", "Bearer " + _key);
            _req.SendWebRequest();
            Status = $"sending {_inFlight}";
        }

        private void Finish(float now, float interval)
        {
            long code = _req.responseCode;
            if (_req.result == UnityWebRequest.Result.Success && code >= 200 && code < 300)
            {
                _queue.RemoveRange(0, Mathf.Min(_inFlight, _queue.Count));
                Sent += _inFlight;
                _dirty = true;
                _backoff = 0f;
                Status = _queue.Count > 0 ? $"sent {Sent}, {_queue.Count} left" : $"sent {Sent}";
                // more waiting: keep going; caught up: wait for the next interval
                _nextTry = now + (_queue.Count > 0 ? 0.25f : interval);
            }
            else
            {
                Failures++;
                string body = _req.downloadHandler != null ? _req.downloadHandler.text : "";
                LastError = $"{code} {_req.error} {Trim(body)}".Trim();
                _backoff = _backoff <= 0f ? MinBackoff : Mathf.Min(MaxBackoff, _backoff * 2f);
                _nextTry = now + _backoff;
                Status = $"retry in {_backoff:0}s";
                if (code == 401 || code == 403 || code == 404)
                    Debug.LogWarning($"[Telemetry] Supabase refused the upload ({LastError}). Check supabaseUrl / supabaseAnonKey and that supabase/schema.sql has been run.");
            }
            _req.Dispose();
            _req = null;
            _inFlight = 0;
        }

        private static string Trim(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 160 ? s.Substring(0, 160) + "…" : s;

        /// Saves the outbox (atomically: a temp file, then swapped in).
        public void Persist()
        {
            if (!_dirty) return;
            _dirty = false;
            try
            {
                if (_queue.Count == 0)
                {
                    if (File.Exists(_outbox)) File.Delete(_outbox);
                    return;
                }
                string tmp = _outbox + ".tmp";
                File.WriteAllLines(tmp, _queue, new UTF8Encoding(false));
                if (File.Exists(_outbox)) File.Delete(_outbox);
                File.Move(tmp, _outbox);
            }
            catch (IOException e) { LastError = e.Message; _dirty = true; }
        }

        public void Dispose()
        {
            if (_req != null)
            {
                // whatever was in flight stays queued: it's only removed on a 2xx
                _req.Abort();
                _req.Dispose();
                _req = null;
            }
            _dirty = true;
            Persist();
        }
    }
}
