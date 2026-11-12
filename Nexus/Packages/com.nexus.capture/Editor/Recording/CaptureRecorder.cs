using System;
using System.IO;
using System.Text;
using Nexus.Core.Services;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEngine;

namespace Nexus.Capture.Editor
{
    /// <summary>
    /// Programmatic recording controller managing Unity Recorder sessions for Capture Mode.
    /// </summary>
    [InitializeOnLoad]
    internal static class CaptureRecorder
    {
        private static RecorderController s_controller;
        private static string s_recordingPath;

        /// <summary>Gets or sets the active resolution preset index from CaptureSettings.</summary>
        internal static int SelectedPreset { get; set; }

        static CaptureRecorder()
        {
            CaptureModeService.RecordToggleRequested += OnRecordToggleRequested;
            CaptureModeService.OpenChanged += OnOpenChanged;
        }

        private static void OnRecordToggleRequested(bool start)
        {
            if (start)
            {
                if (s_controller == null || !s_controller.IsRecording())
                {
                    StartRecording();
                }
            }
            else
            {
                StopRecording();
            }
        }

        private static void OnOpenChanged(bool open)
        {
            // Close or play-mode exit must terminate any ongoing recording session.
            if (!open && (CaptureModeService.IsRecording || (s_controller != null && s_controller.IsRecording())))
            {
                StopRecording();
            }
        }

        private static void StartRecording()
        {
            var settings = CaptureSettings.Load();
            var presets = settings?.ResolutionPresets;
            int width = 1080;
            int height = 1920;

            if (presets != null && presets.Count > 0)
            {
                int index = Math.Max(0, Math.Min(SelectedPreset, presets.Count - 1));
                width = presets[index].width;
                height = presets[index].height;
            }

            int frameRate = settings != null && settings.FrameRate > 0 ? settings.FrameRate : 60;
            string outputFolder = !string.IsNullOrWhiteSpace(settings?.OutputFolder) ? settings.OutputFolder : "Recordings";

            string rawGameName = !string.IsNullOrWhiteSpace(CaptureModeService.Hooks?.GameName)
                ? CaptureModeService.Hooks.GameName
                : Application.productName;
            string gameName = SanitizeFileName(rawGameName);

            string outputBase = null;
            bool started;
            try
            {
                DirectoryInfo projectDir = Directory.GetParent(Application.dataPath);
                string projectRoot = projectDir != null ? projectDir.FullName : Application.dataPath;
                string targetDir = Path.Combine(projectRoot, outputFolder, gameName);

                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                outputBase = Path.Combine(targetDir, timestamp);

                var controllerSettings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
                var movieSettings = ScriptableObject.CreateInstance<MovieRecorderSettings>();
                movieSettings.name = "NexusCaptureMovie";
                movieSettings.Enabled = true;
                movieSettings.OutputFile = outputBase;

                movieSettings.ImageInputSettings = new GameViewInputSettings
                {
                    OutputWidth = width,
                    OutputHeight = height
                };

                movieSettings.AudioInputSettings.PreserveAudio = true;

                movieSettings.EncoderSettings = new CoreEncoderSettings
                {
                    Codec = CoreEncoderSettings.OutputCodec.MP4,
                    EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High
                };

                controllerSettings.AddRecorderSettings(movieSettings);
                controllerSettings.SetRecordModeToManual();
                controllerSettings.FrameRate = frameRate;
                controllerSettings.FrameRatePlayback = FrameRatePlayback.Constant;
                controllerSettings.CapFrameRate = true;

                s_controller = new RecorderController(controllerSettings);
                s_controller.PrepareRecording();
                started = s_controller.StartRecording();
            }
            catch (Exception e)
            {
                // A folder that cannot be created or a Recorder that throws must not leave IsRecording true with nothing recording.
                NexusLog.WarnAlways($"[Nexus Capture] Unity Recorder did not start: {e.Message}");
                started = false;
            }

            if (started)
            {
                s_recordingPath = outputBase + ".mp4";
                NexusLog.WarnAlways($"[Nexus Capture] Recording to {s_recordingPath}");
            }
            else
            {
                NexusLog.WarnAlways("[Nexus Capture] Unity Recorder did not start; nothing is recorded.");
                CaptureModeService.SetRecording(false);
                s_controller = null;
                s_recordingPath = null;
            }
        }

        private static void StopRecording()
        {
            if (s_controller != null)
            {
                if (s_controller.IsRecording())
                {
                    s_controller.StopRecording();
                    if (!string.IsNullOrEmpty(s_recordingPath))
                    {
                        NexusLog.WarnAlways($"[Nexus Capture] Recording saved to {s_recordingPath}");
                    }
                }
                s_controller = null;
                s_recordingPath = null;
            }

            if (CaptureModeService.IsRecording)
            {
                CaptureModeService.SetRecording(false);
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Game";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (Array.IndexOf(invalidChars, c) < 0)
                {
                    sb.Append(c);
                }
            }

            string sanitized = sb.ToString().Trim();
            return string.IsNullOrEmpty(sanitized) ? "Game" : sanitized;
        }
    }
}
