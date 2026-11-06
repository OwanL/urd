
# style guide

- Use plain code documentation, avoid xml docs.
- Code should self documenting, for example, am I able to understand what code does by reading it? ie Session.create() reads quite nicely, where HarnessServiceExecutor.instantiateAsync() means almost nothing to a user without prior knowlege.
- Prefer low levels of scope nesting. Deeply nested code is hard to read and reason about.
- Use same-line opening braces in C# (K&R style).
- Omit braces for short, clear single-statement bodies; keep them when nesting, comments or multiline bodies make grouping clearer.
- One type per file, except small data-only types (records, enums) of one concept, which share a file named for that concept.
- Code should be performant. Urd needs to have a low memory overhead, and be snappy, and quick.
- Follow existing codebase conventions. Ground yourself in this before writing any code.
