# Pi .NET evals

Native behavioral evaluations for the Pi .NET coding agent. The runner is a .NET executable; it
does not load the TypeScript eval package or Vitest.

## Eval suites

Eval definitions are represented by the compiled catalog in `EvalScenarioCatalog.cs`:

- Documentation-lift scenarios compare `without_docs` and `with_docs` in separate Linux containers.
- Host scenarios run on the current machine and are not paired comparisons:
  - `smoke.eval.cs` checks a basic model response and usage.
  - `documentation-audit.eval.cs` audits each Markdown page under
    `src/Pi.CodingAgent/Assets/docs` against the implementation.

The documentation scenarios cover adding a model without replacing existing models, creating a
compiled tool extension, configuring an OpenAI-compatible provider, implementing a custom streaming
provider, and customizing the interactive context footer.

## Run

Run these commands from the repository root. Select a provider and model through both environment
variables or both command-line options:

```powershell
$env:PI_PROVIDER = "openai-codex"
$env:PI_MODEL = "gpt-5.6-sol"
dotnet run --project .\src\Pi.Evals -- host
dotnet run --project .\src\Pi.Evals -- docs
```

Run one suite or filter case names:

```powershell
dotnet run --project .\src\Pi.Evals -- host evals\smoke.eval.cs
dotnet run --project .\src\Pi.Evals -- docs evals\extensions.docs.eval.cs -t "Create and use"
dotnet run --project .\src\Pi.Evals -- docs --runs-per-variant 5
```

`PI_EVAL_RUNS_PER_VARIANT` provides the repetition count when the option is omitted. The `all`
command runs all host evals followed by the documentation comparison. Each successful run writes
protocol, observations, session snapshots, and reports under `src/Pi.Evals/.eval/`.

Documentation evals require Docker, a running Docker daemon, and a stored credential for the selected
provider in `auth.json` under `Config.GetAgentDir()`. The eval runner builds both image variants,
checks that they discover the same cases, and executes each planned arm in a fresh container. A
missing or invalid task report is recorded as an errored arm; blocked pairs make the command exit
nonzero.

## Isolation and limitations

The documentation image is read-only except for `/tmp` and the mounted artifact directory. The
selected provider credential is mounted read-only, copied into a temporary agent directory, consumed
before prompting, and removed before the model-facing tools run. The worker starts as root, protects
the evaluator assembly, then drops to an unprivileged UID/GID. Generated C# extensions are compiled
with the .NET SDK available in the image. The `without_docs` image omits documentation and examples;
the harness also removes the Pi documentation section from its system prompt.

Documentation evals expose file tools only, not shell or web search. This is not a network sandbox:
provider calls and code executed by a generated extension can still access the container network.
Artifacts may contain prompts, responses, generated code, and tool output. Review them before sharing.

Host evals are not containerized. In particular, the documentation audit can read the checked-out
repository source, so run it only with a trusted model/provider configuration.
