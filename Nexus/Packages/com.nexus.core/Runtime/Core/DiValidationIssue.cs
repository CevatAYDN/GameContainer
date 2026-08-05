using System;

namespace Nexus.Core
{
    public enum DiValidationIssueType
    {
        MissingConstructorDependency,
        MissingFieldDependency,
        MissingPropertyDependency,
        MissingMethodDependency,
        CircularDependency,
        UnregisteredViewMediator,
        // A singleton service capturing a transient (non-singleton, non-factory)
        // dependency in its constructor or [Inject] members.
        CaptiveDependency,
    }

    public class DiValidationIssue
    {
        public Type SourceType { get; }
        public Type MissingType { get; }
        public DiValidationIssueType IssueType { get; }
        public string Message { get; }

        public DiValidationIssue(Type sourceType, Type missingType, DiValidationIssueType issueType, string message)
        {
            SourceType = sourceType;
            MissingType = missingType;
            IssueType = issueType;
            Message = message;
        }
    }
}
