namespace Nexus.Core
{
    public interface IView
    {
        void Bind(IContext context);
        void Unbind();
        /// <summary>True if the view instance is valid and alive. Allows non-Unity views to mock validity.</summary>
        bool IsAlive => true;
    }
}
