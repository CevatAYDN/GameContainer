using NUnit.Framework;
using Nexus.Core;
using System;

namespace Nexus.Tests
{
    [TestFixture]
    public class DebugStrippingTests
    {
        public class MockTraceSink : INexusTraceSink
        {
            public int WrittenCount = 0;
            public void Write(in TraceEvent traceEvent)
            {
                WrittenCount++;
            }
        }

        [Test]
        public void Trace_WhenDebugSymbolNotPresent_StripsTracingOverhead()
        {
#if NEXUS_DEBUG
            Assert.Ignore("Test is only valid when NEXUS_DEBUG is not defined.");
#else
            // Reset state
            NexusTrace.Reset();
            
            var sink = new MockTraceSink();
            NexusTrace.AddSink(sink);

            try
            {
                // Call BeginEvent
                int eventId = NexusTrace.BeginEvent(TraceEventType.Signal, "TestSignal");
                
                // Assert that BeginEvent returns 0 (the fallback value when NEXUS_DEBUG is not defined)
                Assert.AreEqual(0, eventId);

                // Call EndEvent
                NexusTrace.EndEvent(eventId);

                // Assert that the sink has NOT received any trace events
                Assert.AreEqual(0, sink.WrittenCount);

                // Assert that GetRecentEvents returns an empty array and count of 0
                var events = NexusTrace.GetRecentEvents(out int count);
                Assert.AreEqual(0, count);
                Assert.IsEmpty(events);
            }
            finally
            {
                NexusTrace.RemoveSink(sink);
            }
#endif
        }
    }
}
