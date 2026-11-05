# Contributing to Control Spawn

Thanks for helping. You don't need fans, RGB or any special hardware to start: the app has a mock mode that fakes all of it.

## Ways to help

- **Report your board.** No coding needed. Fill in the [My board](https://github.com/HarDvolT/control-spawn/issues/new?template=board.yml) form.
- **Try it and report bugs**, especially on hardware we haven't seen.
- **Pick up a [good first issue](https://github.com/HarDvolT/control-spawn/labels/good%20first%20issue).**
- **Talk about bigger ideas** in an issue before you build them. The long-term direction is in [docs/PLAN-platform.md](docs/PLAN-platform.md).
- **Help design the plugin system.** It's being designed in the open in [RFC 0001](docs/rfcs/0001-plugin-system.md), and it has open questions that need experienced people.

## Set up

You need Windows 11, Git and the .NET SDK named in the README.

```powershell
git clone https://github.com/HarDvolT/control-spawn.git
cd control-spawn
dotnet build ControlSpawn.sln -c Debug
dotnet test ControlSpawn.sln -c Debug
```

To run with fake hardware, follow the README's [Build from source](README.md#build-from-source) section.

To look at a page without running the app:

```powershell
dotnet run --project tests/ControlSpawn.App.Tests/Preview -c Debug -- <outdir> --v2 --page=Lighting
```

## Find your way around

[docs/CODE-GUIDE.md](docs/CODE-GUIDE.md) follows one real feature from a slider to a lamp, and shows where everything lives.

## Rules

- **Fan safety comes first.** Never weaken the rules in the README's Safety section. PRs that touch fans, the pump, the fan guard or sensors get extra review.
- **Use `--mock`** unless you know exactly what a change does to real fans or RGB.
- Keep PRs small, with one topic each.
- Match the code around you: dense C#, short comments that explain why, and controls built in code with the theme's styles.
- Build with 0 warnings, and add tests for new logic.

## Pull requests

Branch from `ui/v2-pages`, and open your PR against it. `main` holds released code only. CI must pass, and a maintainer will review it.

By contributing, you agree that your work is licensed under the [Mozilla Public License 2.0](LICENSE).
