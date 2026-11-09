using System;
using System.Collections.Generic;
using Nexus.Core;
#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewCompositionC06Tests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void FailedTransientPostConstruct_ReleasesTheUnreturnedProduct()
        {
            VerifyFailure((container, ledger) =>
            {
                container.Bind<Product>(Lifetime.Transient);
                return container.Resolve<Product>();
            });
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void FailedConstructPostConstruct_ReleasesTheUnreturnedProduct()
        {
            VerifyFailure((container, ledger) => container.Construct<Product>());
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void FailedTypedFactoryPostConstruct_ReleasesTheUnreturnedProduct()
        {
            VerifyFailure((container, ledger) =>
            {
                container.BindTypedFactory<Product>();
                return container.Resolve<IFactory<Product>>().Create();
            });
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void FailedTransientWithOverrides_ReleasesTheUnreturnedProduct()
        {
            VerifyFailure((container, ledger) =>
            {
                container.BindFluent<Product>().AsTransient().WithParameter(ledger);
                return container.Resolve<Product>();
            });
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void FailedTransient_IsNotReinjectedAfterCleanup()
        {
#if UNITY_EDITOR
            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex(@"\[Inject\] dependency '.*LateDependency'.*not registered"));
#endif
            VerifyFailure((container, ledger) =>
            {
                container.Bind<PendingProduct>(Lifetime.Transient);
                return container.Resolve<PendingProduct>();
            }, pending: true);
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void SuccessfullyConstructedProduct_RemainsCallerOwned()
        {
            using var container = new NexusDI();
            var ledger = new Ledger { FailInjection = false };
            container.BindInstance(ledger);
            var product = container.Construct<Product>();
            container.Dispose();
            Require(ledger.Disposals == 0, "A successfully returned product must remain caller-owned.");
            product.Dispose();
            Require(ledger.Disposals == 1, "The caller must still be able to end its product.");
        }

        private static void VerifyFailure(Func<NexusDI, Ledger, object> create, bool pending = false)
        {
            using var container = new NexusDI();
            var ledger = new Ledger();
            container.BindInstance(ledger);
            Exception failure = null;
            try { create(container, ledger); }
            catch (Exception ex) { failure = ex; }
            Require(ReferenceEquals(failure, ledger.Failure), "Preserve the product's original injection exception.");
            Require(ledger.Created == 1 && ledger.Disposals == 1,
                $"An unreturned product must be cleaned once; created={ledger.Created}, disposed={ledger.Disposals}.");
            Require(string.Join(",", ledger.Cleanup) == "deconstruct,dispose", "Run cleanup hooks before disposal.");
            if (pending)
            {
                container.BindInstance(new LateDependency());
                Require(container.ReInjectAll() == 0 && ((PendingProduct)ledger.Last).Late == null,
                    "Late bindings must not inject into an object whose creation already failed.");
            }
            container.Dispose();
            Require(ledger.Disposals == 1, "Container shutdown must not dispose the failed product again.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Ledger
        {
            public readonly Exception Failure = new InvalidOperationException("product injection probe");
            public readonly List<string> Cleanup = new();
            public bool FailInjection = true;
            public int Created, Disposals;
            public Product Last;
        }

        private class Product : IDisposable
        {
            private readonly Ledger _ledger;
            public Product(Ledger ledger)
            {
                _ledger = ledger;
                ledger.Created++;
                ledger.Last = this;
            }
            [PostConstruct] private void Initialize()
            {
                if (_ledger.FailInjection) throw _ledger.Failure;
            }
            [Deconstruct] private void Cleanup() => _ledger.Cleanup.Add("deconstruct");
            public void Dispose()
            {
                _ledger.Disposals++;
                _ledger.Cleanup.Add("dispose");
            }
        }

        private sealed class PendingProduct : Product
        {
            public PendingProduct(Ledger ledger) : base(ledger) { }
            [Inject] public LateDependency Late { get; set; }
        }

        private sealed class LateDependency { }
    }
}
