# Building GeekDesk (fork notes)

This fork (from Baker9017/GeekDesk 2.6.2.2) builds on GitHub Actions with a
Windows runner: nuget restore + MSBuild Release (.NET Framework 4.8).
The workflow file lives at .github/workflows/build.yml and produces a
GeekDesk-Release artifact containing GeekDesk/bin/Release/.

Note: the workflow file may need to be added through the GitHub web UI,
because the Muse GitHub App integration does not have the Workflows
permission required to write files under .github/workflows/.
