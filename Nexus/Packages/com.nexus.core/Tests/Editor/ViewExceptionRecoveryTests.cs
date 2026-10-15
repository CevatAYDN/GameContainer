using System;
using Nexus.Core;
using NUnit.Framework;
using UnityEngine;

namespace Nexus.Editor.Tests
{
    public sealed class ExceptionRecoveryView : View
    {
        public bool FailBind, FailUnbind;
        public int BindCount, CleanupCount;
        protected override void OnBind(IContext context)
        {
            BindCount++;
            if (FailBind) { FailBind = false; throw new InvalidOperationException("bind test"); }
        }
        protected override void OnUnbind()
        {
            CleanupCount++;
            if (FailUnbind) { FailUnbind = false; throw new InvalidOperationException("unbind test"); }
        }
    }
    public sealed class ViewExceptionRecoveryTests
    {
        private GameObject _object;
        private ExceptionRecoveryView _view;
        [SetUp] public void Setup() { _object = new GameObject("lifecycle regression"); _view = _object.AddComponent<ExceptionRecoveryView>(); }
        [TearDown] public void Cleanup() { _view.Unbind(); UnityEngine.Object.DestroyImmediate(_object); }
        [Test] public void UnbindFailureDoesNotPreventNextBind()
        {
            _view.Bind(null); _view.FailUnbind = true;
            Assert.Throws<InvalidOperationException>(() => _view.Unbind());
            _view.Bind(null);
            Assert.That(_view.BindCount, Is.EqualTo(2));
        }
        [Test] public void BindFailureCleansPartialSetupAndCanRetry()
        {
            _view.FailBind = true;
            Assert.Throws<InvalidOperationException>(() => _view.Bind(null));
            _view.Bind(null);
            Assert.That(_view.BindCount, Is.EqualTo(2));
            Assert.That(_view.CleanupCount, Is.EqualTo(1));
        }
        [Test] public void UnbindIsIdempotent()
        {
            _view.Bind(null); _view.Unbind(); _view.Unbind();
            Assert.That(_view.CleanupCount, Is.EqualTo(1));
        }
    }
}

