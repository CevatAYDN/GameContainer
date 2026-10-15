namespace Nexus.Core
{
    /// <summary>
    /// Creates <typeparamref name="TValue"/> instances on demand. Bind one with
    /// <c>builder.BindTypedFactory&lt;TValue&gt;()</c> (the container constructs and injects each
    /// instance) or <c>builder.BindTypedFactory&lt;TValue&gt;(() =&gt; ...)</c> (your delegate creates it),
    /// then take it as a dependency: <c>[Inject] public IFactory&lt;Enemy&gt; Enemies { get; set; }</c>.
    /// Every <see cref="Create"/> call returns a new instance that the container does not own.
    /// </summary>
    public interface IFactory<out TValue>
    {
        TValue Create();
    }

    /// <summary>
    /// Creates <typeparamref name="TValue"/> instances from one runtime argument. With
    /// <c>BindTypedFactory&lt;TParam, TValue&gt;()</c> the argument is passed to the constructor parameter
    /// whose type is <typeparamref name="TParam"/>; the remaining parameters and members are resolved
    /// from the container.
    /// </summary>
    public interface IFactory<in TParam, out TValue>
    {
        TValue Create(TParam param);
    }

    /// <summary>
    /// Creates <typeparamref name="TValue"/> instances from two runtime arguments, matched to constructor
    /// parameters by type. Use the delegate overload when both arguments have the same type.
    /// </summary>
    public interface IFactory<in TParam1, in TParam2, out TValue>
    {
        TValue Create(TParam1 param1, TParam2 param2);
    }
}
