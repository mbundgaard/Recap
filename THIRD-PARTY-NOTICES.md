# Third-party components

Recap's own source code is licensed under MIT. This does not relicense its dependencies,
third-party development material, or the Microsoft SDK/runtime components in a
self-contained executable.

| Component | Direct version | Licence / terms |
| --- | --- | --- |
| NAudio | 2.2.1 | MIT; retain the package's copyright and licence notices |
| Markdig | 1.4.0 | BSD-2-Clause; retain the package's copyright and licence notices |
| Microsoft Cognitive Services Speech SDK | 1.52.0 | Microsoft Software License Terms, included as `LICENSE.txt` in the NuGet package; not covered by Recap's MIT licence |
| .NET / WPF / ASP.NET Core runtime | .NET 8 runtime resolved when publishing | See the runtime's licence and third-party notices |
| Frontend design skill | Repository copy | Apache-2.0; see `.agents/skills/frontend-design/LICENSE.txt` |
| pyannote speaker-diarization-community-1 model bundle | Repository copy in `models/community-1` | CC BY 4.0 as declared in the upstream model card; see `models/community-1/NOTICE.md` and `LICENSE-CC-BY-4.0.txt` |

Source packages and authoritative licence information:

- <https://www.nuget.org/packages/NAudio/2.2.1>
- <https://www.nuget.org/packages/Markdig/1.4.0>
- <https://www.nuget.org/packages/Microsoft.CognitiveServices.Speech/1.52.0>
- <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>
- <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>
- <https://github.com/dotnet/wpf/blob/main/LICENSE.TXT>
- <https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt>
- <https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.txt>

This list identifies the direct dependencies; it is not a completed redistribution
licence audit. Transitive dependencies and native components have their own terms.
Inspect the resolved NuGet packages and runtime packs, retain all required notices,
and review the Speech SDK's distribution/end-user terms before publicly distributing
binaries. The release workflow therefore creates drafts for maintainer review.

## Bundled Community-1 models

The Community-1 pipeline and bundled segmentation, speaker embedding and PLDA files
are third-party model materials, not MIT-licensed Recap source. Their publisher is
pyannote; the original model card is at
<https://huggingface.co/pyannote/speaker-diarization-community-1>.

Retain [`models/community-1/NOTICE.md`](models/community-1/NOTICE.md), the upstream
model cards and the [CC BY 4.0 legal text](models/community-1/LICENSE-CC-BY-4.0.txt)
when redistributing the model bundle. The notice credits the segmentation authors,
WeSpeaker/VoxCeleb embedding work and BUT Speech@FIT PLDA/VBx contributors, identifies
Recap's packaging additions, and records the limits of the component license
information supplied upstream. Distributing the models does not imply endorsement
by their authors. Recap's MIT license does not replace their license.

Python packages and native libraries installed for local recognition have their own
licenses and notices. The model attribution above is not a complete redistribution
audit of that runtime or its transitive dependencies.

Cloud service subscriptions, charges and service terms are separate from the source
code licence. Recap does not include shared service credentials.
