using System.Collections;
using NUnit.Framework;
using Nexus.Ads.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Nexus.Ads.Tests.Runtime
{
    /// <summary>Issue #430: the UI Toolkit consent screen as a player draws it.</summary>
    public sealed class GameConsentScreenPlayModeTests
    {
        [UnityTest]
        public IEnumerator TheScreen_ShowsOneDocumentWithBothChoices_AndIsRemovedOnceTheyChoose()
        {
            var screen = new UitkConsentScreen();
            bool? choice = null;
            screen.Show(new ConsentTexts { Title = "T", Body = "B", Accept = "Accept", Reject = "Reject" }, accepted => choice = accepted);
            yield return null;

            var document = Object.FindFirstObjectByType<UIDocument>();
            Assert.IsNotNull(document, "the screen is a UI Toolkit document");
            var accept = document.rootVisualElement.Q<Button>(ConsentScreenNames.Accept);
            Assert.IsNotNull(accept);
            Assert.IsNotNull(document.rootVisualElement.Q<Button>(ConsentScreenNames.Reject));
            Assert.IsNull(choice, "nothing is chosen for the player");
            Assert.IsNotNull(document.panelSettings.themeStyleSheet, "without a theme the panel draws no text");

            screen.Show(new ConsentTexts { Accept = "a", Reject = "r" }, _ => { }); // a second Show replaces the first
            yield return null;
            Assert.AreEqual(1, Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None).Length);

            screen.Hide();
            yield return null;
            Assert.IsNull(Object.FindFirstObjectByType<UIDocument>());
        }
    }
}
