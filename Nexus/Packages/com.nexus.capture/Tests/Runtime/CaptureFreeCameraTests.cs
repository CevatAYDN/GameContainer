using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Capture.Tests
{
    /// <summary>
    /// PlayMode tests locking free camera lifecycle, fallback camera creation, and cleanup.
    /// </summary>
    public sealed class CaptureFreeCameraTests
    {
        private readonly List<UnityEngine.Object> _createdObjects = new List<UnityEngine.Object>();

        [TearDown]
        public void Teardown()
        {
            CaptureFreeCamera.Restore();
            CaptureModeService.Close();

            for (int i = 0; i < _createdObjects.Count; i++)
            {
                if (_createdObjects[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_createdObjects[i]);
                }
            }
            _createdObjects.Clear();
        }

        private T Track<T>(T obj) where T : UnityEngine.Object
        {
            if (obj != null)
            {
                _createdObjects.Add(obj);
            }
            return obj;
        }

        [UnityTest]
        public IEnumerator FreeCamera_Restore_ReenablesMainCameraAndDestroysItsCamera()
        {
            var goMain = Track(new GameObject("MainCamera"));
            goMain.tag = "MainCamera";
            var mainCam = goMain.AddComponent<Camera>();
            mainCam.enabled = true;

            CaptureModeService.Open();
            CaptureFreeCamera.Toggle();

            Assert.That(CaptureFreeCamera.Active, Is.True, "Free camera must be active after toggle.");
            Assert.That(mainCam.enabled, Is.False, "Fallback path must disable the main camera component.");
            var freeCam = CaptureFreeCamera.CameraObject;
            Assert.That(freeCam, Is.Not.Null, "Free camera GameObject must exist.");

            CaptureModeService.Close();

            yield return null;

            Assert.That(CaptureFreeCamera.Active, Is.False, "Free camera must be inactive after Close().");
            Assert.That(mainCam.enabled, Is.True, "Main camera component must be re-enabled after restore.");
            Assert.That(freeCam == null || freeCam.Equals(null), Is.True, "Free camera GameObject must be destroyed.");
        }

        [Test]
        public void FreeCamera_WithoutMainCamera_StaysOffAndWarns()
        {
            var existingMain = Camera.main;
            string originalTag = null;
            if (existingMain != null)
            {
                originalTag = existingMain.gameObject.tag;
                existingMain.gameObject.tag = "Untagged";
            }

            try
            {
                CaptureModeService.Open();
                LogAssert.Expect(LogType.Warning, new Regex("Nexus Capture"));

                CaptureFreeCamera.Toggle();

                Assert.That(CaptureFreeCamera.Active, Is.False, "Free camera must stay off when no Camera.main exists.");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (existingMain != null && originalTag != null)
                {
                    existingMain.gameObject.tag = originalTag;
                }
            }
        }
    }
}
