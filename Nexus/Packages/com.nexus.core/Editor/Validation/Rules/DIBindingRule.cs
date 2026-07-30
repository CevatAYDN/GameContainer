using System.Collections.Generic;

namespace Nexus.Editor.Validation.Rules
{
    /// <summary>
    /// Validates Dependency Injection (DI) bindings and container integrity pre-compile.
    /// </summary>
    public sealed class DIBindingRule : IBuildValidationRule
    {
        public string Name => "DI Binding Rule";

        public IEnumerable<ValidationIssue> Evaluate()
        {
            var issues = new List<ValidationIssue>();

            // DI binding validation is now handled directly in BuildValidation.ValidateDiBindings()
            // This rule class is kept as an extension point for future modular validation rules.
            // If you add a new validation, implement it here instead of modifying BuildValidation.cs.

            return issues;
        }
    }
}
