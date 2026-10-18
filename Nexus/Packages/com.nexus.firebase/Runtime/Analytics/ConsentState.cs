namespace Nexus.Firebase.Analytics
{
    /// <summary>
    /// The consent answer the analytics rule reads. It mirrors the User Messaging Platform's status without referencing its SDK, so the
    /// rule compiles and is tested without any SDK installed.
    /// </summary>
    public enum ConsentState
    {
        /// <summary>No consent update has answered yet: a first launch that is offline, or before the first update returns.</summary>
        Unknown,

        /// <summary>Consent is required in this region and the player has not answered (the EEA and the UK).</summary>
        Required,

        /// <summary>Consent is not required in this region.</summary>
        NotRequired,

        /// <summary>The player answered the consent form.</summary>
        Obtained,
    }
}
