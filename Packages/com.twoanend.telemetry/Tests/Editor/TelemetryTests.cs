using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace TwoAnEnd.Playtest.Tests
{
    public class JsonTests
    {
        [Test]
        public void EscapesStringsSoALineCantBreak()
        {
            string s = Json.Write("a \"quote\", a \\ slash,\na newline\tand \u0001");
            Assert.AreEqual("\"a \\\"quote\\\", a \\\\ slash,\\na newline\\tand \\u0001\"", s);
            Assert.IsFalse(s.Contains("\n"));
        }

        [Test]
        public void NumbersIgnoreTheMachinesCulture()
        {
            var was = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.AreEqual("1.5", Json.Write(1.5f));
                Assert.AreEqual("-0.25", Json.Write(-0.25));
            }
            finally { Thread.CurrentThread.CurrentCulture = was; }
        }

        [Test]
        public void NonFiniteNumbersBecomeNull()
        {
            Assert.AreEqual("null", Json.Write(float.NaN));
            Assert.AreEqual("null", Json.Write(double.PositiveInfinity));
        }

        [Test]
        public void WritesNestedObjectsAndArrays()
        {
            var d = new Dictionary<string, object>
            {
                { "lane", 2 }, { "ok", true }, { "pts", new[] { 1f, 2.5f } },
                { "pos", new Vector3(1, 2, 3) }, { "inner", new Dictionary<string, object> { { "x", null } } },
                { "raw", new RawJson("{\"a\":1}") },
            };
            Assert.AreEqual("{\"lane\":2,\"ok\":true,\"pts\":[1,2.5],\"pos\":[1,2,3],\"inner\":{\"x\":null},\"raw\":{\"a\":1}}", Json.Write(d));
        }

        [Test]
        public void PairsKeepOrderAndToleratesAnOddCount()
        {
            var d = Json.Pairs(new object[] { "b", 1, "a", 2, "dangling" });
            CollectionAssert.AreEqual(new[] { "b", "a", "dangling" }, new List<string>(d.Keys));
            Assert.IsNull(d["dangling"]);
        }
    }

    public class PerfStatsTests
    {
        [Test]
        public void SteadySixtyFps()
        {
            var frames = new List<float>();
            for (int i = 0; i < 600; i++) frames.Add(1000f / 60f);
            var s = PerfStats.Summarise(frames);
            Assert.AreEqual(60f, s.avgFps, 0.01f);
            Assert.AreEqual(60f, s.low1Fps, 0.01f);
            Assert.AreEqual(600, s.frames);
        }

        [Test]
        public void StuttersShowInTheLowsNotTheAverage()
        {
            // 990 smooth frames and 10 bad ones: the average barely moves, the 1% low craters
            var frames = new List<float>();
            for (int i = 0; i < 990; i++) frames.Add(10f);
            for (int i = 0; i < 10; i++) frames.Add(100f);
            var s = PerfStats.Summarise(frames);
            Assert.Greater(s.avgFps, 90f);
            Assert.AreEqual(10f, s.low1Fps, 0.01f);
            Assert.AreEqual(100f, s.maxMs);
            Assert.AreEqual(10f, s.p50Ms);
        }

        [Test]
        public void NearestRankPercentile()
        {
            var sorted = new float[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            Assert.AreEqual(5f, PerfStats.Percentile(sorted, 0.5));
            Assert.AreEqual(10f, PerfStats.Percentile(sorted, 0.99));
            Assert.AreEqual(1f, PerfStats.Percentile(sorted, 0.0));
        }

        [Test]
        public void EmptyIsZeroNotACrash()
        {
            var s = PerfStats.Summarise(new List<float>());
            Assert.AreEqual(0, s.frames);
            Assert.AreEqual(0f, s.avgFps);
        }
    }

    public class SessionTests
    {
        [Test]
        public void CodesAreShortReadableAndStable()
        {
            string id = "3f2a9c0e4b8d4e6f9a1b2c3d4e5f6a7b";
            string code = SessionInfo.CodeFor(id);
            Assert.AreEqual(code, SessionInfo.CodeFor(id));
            Assert.AreEqual(7, code.Length);
            Assert.AreEqual('-', code[4]);
            foreach (char c in code.Replace("-", "")) StringAssert.DoesNotContain(c.ToString(), "0O1IL");
        }

        [Test]
        public void DifferentSessionsGetDifferentCodes()
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < 500; i++) seen.Add(SessionInfo.CodeFor(SessionInfo.NewSessionId()));
            Assert.Greater(seen.Count, 495);
        }
    }
}
