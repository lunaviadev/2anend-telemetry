using System;
using System.IO;
using System.Text;

namespace TwoAnEnd.Playtest
{
    /// One JSON object per line (JSONL), appended to a file per session. A crash or a pulled
    /// plug loses at most the last second, and every line before it still parses.
    public sealed class JsonlSink : IDisposable
    {
        public string FilePath { get; }
        public long Lines { get; private set; }
        public long Bytes { get; private set; }
        private StreamWriter _w;

        public JsonlSink(string dir, string fileName)
        {
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, fileName);
            var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _w = new StreamWriter(fs, new UTF8Encoding(false), 16 * 1024);
        }

        public void Write(string line)
        {
            if (_w == null) return;
            _w.Write(line);
            _w.Write('\n');
            Lines++;
            Bytes += line.Length + 1;
        }

        public void Flush()
        {
            try { _w?.Flush(); } catch (IOException) { }
        }

        public void Dispose()
        {
            if (_w == null) return;
            Flush();
            _w.Dispose();
            _w = null;
        }
    }
}
