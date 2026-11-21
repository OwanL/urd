namespace Urd.Core.Context;

internal static class SkillPruningGuidance {
  internal const string Text = """
      Select the specialized skills that are relevant to the task. Keep a skill when its instructions may materially help.
      Each file has a separate opaque candidate ID. The directory identifies the skill's source, not a strict applicability scope; packaged skills can be useful elsewhere.
      Choose by name, description and path when sufficient. DescriptionRef identifies an identical description, which may itself be omitted; it does not imply identical skill bodies.
      """;
}
