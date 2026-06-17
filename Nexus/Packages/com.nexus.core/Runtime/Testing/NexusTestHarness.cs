namespace Nexus.Core
{
    public static class NexusTestHarness
    {
        public static NexusTestContext CreateContext()
        {
            var context = new Context(parent: null, contextData: null);
            return new NexusTestContext(context);
        }
    }
}
