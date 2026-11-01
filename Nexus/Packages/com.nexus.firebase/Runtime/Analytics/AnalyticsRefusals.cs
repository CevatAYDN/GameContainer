using System.Collections.Generic;
using Nexus.Core.Services;

namespace Nexus.Firebase.Analytics
{
    /// <summary>
    /// Reports what Firebase would refuse, once per name, in every build (<see cref="NexusLog.WarnAlways"/>): a refused name never reaches
    /// the SDK, and the SDK logs nothing for what it is not given, so this line is the only trace on the device (issue #123). The Firebase
    /// service and the mock share it, so the editor shows the same lines a player writes.
    /// </summary>
    public sealed class AnalyticsRefusals
    {
        private readonly HashSet<string> _reported = new HashSet<string>();

        /// <summary>True when Firebase accepts the event name; otherwise reports it once.</summary>
        public bool AcceptEvent(string name)
        {
            return Accept("event '" + name + "' is not sent: ", AnalyticsEventName.EventRefusal(name));
        }

        /// <summary>True when Firebase accepts the parameter's name and has a type for its value; otherwise reports it once.</summary>
        public bool AcceptParameter(string eventName, string name, object value)
        {
            string refusal = AnalyticsEventName.ParameterRefusal(name);
            if (refusal == null && AnalyticsParameterValue.KindOf(value) == AnalyticsParameterKind.Unsupported)
            {
                refusal = "Firebase has no type for its value (" + (value == null ? "null" : value.GetType().FullName) + ")";
            }

            return Accept("parameter '" + name + "' of event '" + eventName + "' is not sent: ", refusal);
        }

        /// <summary>True when Firebase accepts the user property's name and value; otherwise reports it once.</summary>
        public bool AcceptUserProperty(string name, string value)
        {
            return Accept("user property '" + name + "' is not set: ", AnalyticsUserProperty.NameRefusal(name) ?? AnalyticsUserProperty.ValueRefusal(value));
        }

        private bool Accept(string subject, string refusal)
        {
            if (refusal == null)
            {
                return true;
            }

            string message = "[Firebase] Analytics " + subject + refusal;
            if (_reported.Add(message))
            {
                NexusLog.WarnAlways(message);
            }

            return false;
        }
    }
}
