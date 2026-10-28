using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    // Unity passes UnityLinker the link.xml files under Assets only. The packages' own files were never read until
    // NexusPackagesLinkXml handed them over (issue #110): at High stripping com.nexus.firebase, com.nexus.iap.unity and
    // Nexus.Ads.Bridge.Nexus were removed whole despite preserve="all".
    [TestFixture]
    public class PackagesLinkXmlTests
    {
        private string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "nexus-linkxml-" + System.Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private void WriteFile(string relativePath, string text)
        {
            string path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        [Test]
        public void EveryPackageLinkXmlReachesTheLinkerOnceAndUnimportedOrBrokenFilesDoNot()
        {
            WriteFile("com.nexus.a/Runtime/link.xml", "<linker><assembly fullname=\"A\" preserve=\"all\" ignoreIfMissing=\"true\"/></linker>");
            WriteFile("com.nexus.b/link.xml", "<linker><assembly fullname=\"B\"><namespace fullname=\"B.Services\" preserve=\"all\"/></assembly></linker>");
            WriteFile("com.nexus.a/Samples~/Demo/link.xml", "<linker><assembly fullname=\"Sample\" preserve=\"all\"/></linker>");
            WriteFile("com.nexus.b/Broken/link.xml", "<linker><assembly fullname=\"Broken\"");
            LogAssert.Expect(LogType.Warning, new Regex(@"Broken[\\/]link\.xml is not a valid link\.xml"));

            string output = Path.Combine(_root, "out", "PackagesLink.xml");
            string merged = NexusPackagesLinkXml.Merge(new[] { Path.Combine(_root, "com.nexus.a"), Path.Combine(_root, "com.nexus.b") }, output);

            var assemblies = XDocument.Load(merged).Root.Elements("assembly").Select(a => (string)a.Attribute("fullname")).ToList();
            CollectionAssert.AreEquivalent(new[] { "A", "B" }, assemblies);
            Assert.That(File.ReadAllText(merged), Does.Contain("B.Services").And.Contain("ignoreIfMissing=\"true\""));
        }

        // Only the core is installed in every project that runs these tests; harness CG7 checks each package's own file.
        // Unity finds linker processors through TypeCache and calls them through the interface; so does this test (#115).
        [Test]
        public void TheInstalledCorePackagePreservesItsServicesThroughTheLinker()
        {
            Type processorType = TypeCache.GetTypesDerivedFrom<IUnityLinkerProcessor>().SingleOrDefault(t => t == typeof(NexusPackagesLinkXml));
            Assert.That(processorType, Is.Not.Null, "Unity does not find NexusPackagesLinkXml as an IUnityLinkerProcessor");
            var processor = (IUnityLinkerProcessor)Activator.CreateInstance(processorType);
            string merged = processor.GenerateAdditionalLinkXmlFile(null, null);

            var assemblies = XDocument.Load(merged).Root.Elements("assembly").Select(a => (string)a.Attribute("fullname")).ToList();
            CollectionAssert.IsSubsetOf(new[] { "com.nexus.core", "com.nexus.core.dots" }, assemblies);
            Assert.That(File.ReadAllText(merged), Does.Contain("<namespace fullname=\"Nexus.Core.Services\" preserve=\"all\""));
        }
    }
}
