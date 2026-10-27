# XML API documentation

The Core, Kernel, Kernel generator/analyzer, and built-in module libraries emit XML documentation files as part of their builds. DocFX uses those files for the generated API reference. CI builds every library and checks that the XML documentation exists and that the system-author-facing host, population, and scheduling contracts have summaries.

The existing libraries expose a much larger legacy surface than the system-author workflow uses. Requiring a one-line XML comment for every existing public member would add a large volume of low-value boilerplate. The checker therefore enforces a focused list of author-facing contracts in `docs/check_api_xml_docs.py`; other public XML entries are still generated, and their comments can be expanded when those APIs are revised.

When adding or changing a public system-author API, write a summary that explains what the caller can do or rely on. Add lifetime, scheduling, access, or rollback constraints as remarks when they affect correct use. Add the symbol to the checker when it is part of the host, population, or scheduling contract, and update the relevant guide with a real usage path. Avoid summaries that only restate a method name.

Build the public libraries first, then run the checker and the pinned DocFX tool from the repository root:

```sh
dotnet build src/Systemic.Kernel/Systemic.Kernel.csproj -c Release
dotnet build src/Systemic.Core/Systemic.Core.csproj -c Release
dotnet build src/Systemic.Kernel.Generator/Systemic.Kernel.Generator.csproj -c Release
dotnet build src/modules/Systemic.Rendering/Systemic.Rendering.csproj -c Release
dotnet build src/modules/Systemic.Incremental/Systemic.Incremental.csproj -c Release
dotnet build src/modules/Systemic.Presentation/Systemic.Presentation.csproj -c Release
dotnet build src/modules/Systemic.Gameplay2D/Systemic.Gameplay2D.csproj -c Release
dotnet build tests/Systemic.Core.Tests/Systemic.Core.Tests.csproj -c Release
python3 docs/check_api_xml_docs.py --configuration Release
dotnet tool restore
dotnet tool run docfx -- metadata docs/docfx.json
dotnet tool run docfx -- build docs/docfx.json
```
