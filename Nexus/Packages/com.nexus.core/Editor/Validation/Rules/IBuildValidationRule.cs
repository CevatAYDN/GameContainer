using System.Collections.Generic;

namespace Nexus.Editor.Validation.Rules
{
    public struct ValidationIssue
    {
        public string RuleName;
        public string Message;
        public bool IsError;
    }

    /// <summary>
    /// Represents a modular pre-compile build validation rule evaluating framework integrity.
    /// Follows SOLID principles to allow adding new validation rules without modifying BuildValidation.cs.
    /// </summary>
    public interface IBuildValidationRule
    {
        string Name { get; }
        IEnumerable<ValidationIssue> Evaluate();
    }
}
