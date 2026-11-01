using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Firebase.Crash;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Firebase.Tests
{
    /// <summary>
    /// Issue #199: <see cref="ICrashReporter"/> had no custom keys or log lines, so a crash from a shipped player arrived with no
    /// level, build flavour, consent state or last screen. Services initialize one by one in bind order, so a service bound before
    /// the reporter sets its keys before the SDK is ready; a key lost there is lost without a word.
    /// </summary>
    public sealed class CrashContextTests
    {
        [Test]
        public void WhatIsSetBeforeTheReporterIsReady_ReachesItOnce_EachKeyWithItsLastValue()
        {
            var context = new CrashContext();
            context.SetCustomKey("level", "1");
            context.Log("boot");
            context.SetCustomKey("level", "2");
            context.Log("menu");
            LogAssert.Expect(LogType.Warning, new Regex(@"^\[CrashReporter\] A custom key needs a name: the value 'x' was not set\.$"));
            context.SetCustomKey("", "x");

            var keys = new List<string>();
            var lines = new List<string>();
            context.Open((key, value) => keys.Add(key + "=" + value), lines.Add);
            context.SetCustomKey("screen", "shop");
            context.Log("shop opened");

            CollectionAssert.AreEqual(new[] { "level=2", "screen=shop" }, keys);
            CollectionAssert.AreEqual(new[] { "boot", "menu", "shop opened" }, lines);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void BeforeTheReporterIsReady_OnlyTheNewestLinesAreHeld_AndNothingOnceItWillNeverBe()
        {
            var context = new CrashContext();
            for (int i = 0; i <= CrashContext.HeldLines; i++) context.Log("line " + i);
            var lines = new List<string>();
            context.Open((key, value) => { }, lines.Add);
            Assert.AreEqual(CrashContext.HeldLines, lines.Count);
            Assert.AreEqual("line 1", lines[0], "the oldest line went first");

            var closed = new CrashContext();
            closed.SetCustomKey("level", "1");
            closed.Log("boot");
            closed.Close();
            var reached = new List<string>();
            closed.Open((key, value) => reached.Add(key), reached.Add);
            CollectionAssert.IsEmpty(reached, "an unavailable SDK drops what was held");
        }
    }
}
