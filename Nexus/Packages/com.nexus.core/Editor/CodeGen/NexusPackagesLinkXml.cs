using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace Nexus.Editor
{
    /// <summary>
    /// Hands the Nexus packages' link.xml files to UnityLinker. Unity passes the linker the link.xml files under Assets only;
    /// a package's own file reaches it through a processor like this one, as the Input System and Unity Services packages
    /// do. A game's generated link.xml lists no package type (issue #110), so without this, managed stripping above Minimal
    /// removes the package types that the reflection path constructs.
    /// </summary>
    public sealed class NexusPackagesLinkXml : IUnityLinkerProcessor
    {
        private const string OutputPath = "Library/Nexus/PackagesLink.xml";

        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
            => Merge(UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Where(p => p.name.StartsWith("com.nexus.", StringComparison.Ordinal))
                .Select(p => p.resolvedPath), OutputPath);

        /// <summary>
        /// Writes the elements of every link.xml under <paramref name="packageRoots"/> into one linker file at
        /// <paramref name="outputPath"/> and returns its full path, or null when there is none. Folders ending in '~' are not
        /// imported by Unity and are skipped. A file that does not parse is skipped with a warning: it loses its own
        /// preservation, never the player build.
        /// </summary>
        internal static string Merge(IEnumerable<string> packageRoots, string outputPath)
        {
            var linker = new XElement("linker");
            var merged = new List<string>();
            foreach (string root in packageRoots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (string file in Directory.GetFiles(root, "link.xml", SearchOption.AllDirectories))
                {
                    if (Path.GetRelativePath(root, file).Split('/', '\\').Any(part => part.EndsWith("~", StringComparison.Ordinal))) continue;
                    try
                    {
                        linker.Add(XDocument.Load(file).Root.Elements());
                        merged.Add(file);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Nexus] {file} is not a valid link.xml and is not handed to the linker: {ex.Message}");
                    }
                }
            }
            if (merged.Count == 0) return null;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            new XDocument(linker).Save(outputPath);
            string fullPath = Path.GetFullPath(outputPath);
            Debug.Log($"[Nexus] {merged.Count} package link.xml files handed to the linker as {fullPath}");
            return fullPath;
        }
    }
}
