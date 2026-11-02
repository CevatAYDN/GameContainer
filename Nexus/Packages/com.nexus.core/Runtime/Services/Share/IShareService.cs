namespace Nexus.Core.Services
{
    /// <summary>
    /// Hands a text or a file to the platform's share sheet (#134). Android only for now: elsewhere a call
    /// shares nothing and returns false.
    /// </summary>
    public interface IShareService
    {
        /// <summary>Opens the share sheet with <paramref name="text"/> under the sheet's <paramref name="title"/>.
        /// True when the sheet opened; in the editor the call is logged and returns false.</summary>
        bool ShareText(string text, string title);

        /// <summary>
        /// Opens the share sheet with the file at <paramref name="path"/>, typed <paramref name="mimeType"/>
        /// (for example <c>application/json</c>, <c>image/png</c>). The file must lie under
        /// <c>Application.persistentDataPath</c>, <c>Application.temporaryCachePath</c> or the app's internal
        /// files or cache folder; the chosen app gets read access to that one file. True when the sheet opened;
        /// a missing file, a file outside those folders or an unsupported platform returns false with a warning
        /// in every build, and the editor logs the call and returns false.
        /// </summary>
        bool ShareFile(string path, string mimeType, string title);
    }
}
