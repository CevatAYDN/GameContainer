using System;
using Nexus.Ads.Editor;
using Nexus.Ads.Unity;
using NUnit.Framework;
using UnityEditor;

namespace Nexus.Ads.Tests
{
    public sealed class AdsTabsTests
    {
        [Test]
        public void TheSettingsAndSetupTabs_BuildForEveryMediation()
        {
            // the keys, the guide, Prepare Android and the privacy fields are built only for the selected mediation (#193),
            // and the window's every-tab test sees the one the project's asset holds
            bool created = AssetDatabase.LoadAssetAtPath<AdsSettings>(AdsProjectActions.AssetPath) == null;
            var settings = AdsProjectActions.LoadOrCreate();
            string saved = EditorJsonUtility.ToJson(settings);
            try
            {
                foreach (AdProviderKind kind in Enum.GetValues(typeof(AdProviderKind)))
                {
                    settings.SelectedMediation = kind;
                    Assert.IsNotNull(new AdsSettingsPlugin().CreateView(), kind + ": Settings");
                    Assert.IsNotNull(new AdsSetupPlugin().CreateView(), kind + ": Setup");
                }
            }
            finally
            {
                if (created) AssetDatabase.DeleteAsset(AdsProjectActions.AssetPath);
                else EditorJsonUtility.FromJsonOverwrite(saved, settings);
            }
        }
    }
}
