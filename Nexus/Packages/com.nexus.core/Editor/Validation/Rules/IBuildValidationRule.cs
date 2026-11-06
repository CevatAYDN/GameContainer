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
    /// A project's own validation rule. <see cref="BuildValidation.Validate"/> creates every non-abstract implementation with a
    /// public parameterless constructor, in any editor assembly, and reports its issues under its <see cref="Name"/>: an error fails
    /// the validation, and so a player build, as the built-in rules do. A rule that throws fails the run; a rule without such a
    /// constructor is not run, and the validation lists a warning naming it.
    /// </summary>
    public interface IBuildValidationRule
    {
        string Name { get; }
        IEnumerable<ValidationIssue> Evaluate();
    }
}
