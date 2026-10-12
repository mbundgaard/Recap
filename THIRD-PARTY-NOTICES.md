# Third-party components

Recap's own source code is licensed under MIT. This does not relicense its dependencies,
third-party development material, or the Microsoft SDK/runtime components in a
self-contained executable.

| Component | Direct version | Licence / terms |
| --- | --- | --- |
| NAudio | 2.2.1 | MIT; retain the package's copyright and licence notices |
| Markdig | 1.4.0 | BSD-2-Clause; retain the package's copyright and licence notices |
| Microsoft Cognitive Services Speech SDK | 1.52.0 | Microsoft Software License Terms, included as `LICENSE.txt` in the NuGet package; not covered by Recap's MIT licence |
| .NET / WPF runtime | .NET 8 runtime resolved when publishing | See the runtime's licence and third-party notices |
| Frontend design skill | Repository copy | Apache-2.0; see `.agents/skills/frontend-design/LICENSE.txt` |

Source packages and authoritative licence information:

- <https://www.nuget.org/packages/NAudio/2.2.1>
- <https://www.nuget.org/packages/Markdig/1.4.0>
- <https://www.nuget.org/packages/Microsoft.CognitiveServices.Speech/1.52.0>
- <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>
- <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>
- <https://github.com/dotnet/wpf/blob/main/LICENSE.TXT>

This list identifies the direct dependencies; it is not a completed redistribution
licence audit. Transitive dependencies and native components have their own terms.
Inspect the resolved NuGet packages and runtime packs, retain all required notices,
and review the Speech SDK's distribution/end-user terms before publicly distributing
binaries. The release workflow therefore creates drafts for maintainer review.

Cloud service subscriptions, charges and service terms are separate from the source
code licence. Recap does not include shared service credentials.
