# Local Whisper dependency security review

Review date: **2026-10-16** (local system date).

## Snapshot and scope

The reviewed GitHub Dependabot snapshot contained **23 open alerts, including 14 high-severity alerts, representing 9 unique advisories across 3 manifests**:

- `src/Recap/LocalWhisper/requirements.in`
- `src/Recap/LocalWhisper/requirements-cpu.txt`
- `src/Recap/LocalWhisper/requirements-cuda.txt`

Several alerts report the same advisory in different manifests. Counts describe this snapshot, not a live status or 23 distinct vulnerabilities.

Review covered Recap's worker and installed CPU runtime source under `%LOCALAPPDATA%\Recap\LocalWhisper\runtime\cpu-74ABF4FF0E270D2F\Lib\site-packages`, including WhisperX 3.8.6, pyannote.audio 4.0.7, NLTK 3.10.3 and PyTorch 2.8.0+cpu. It considered the fixed English/Danish alignment models, CTranslate2 transcription and local community-1 diarization. It is not a complete transitive reachability proof, a sandbox assessment, or GPU validation.

**Dependency locks remain within affected version ranges.** No alerts were dismissed by this review. Application-level constraints and mitigation below are not upstream patches or a declaration that the dependency stack is safe. Release publication remains pending maintainer review.

## Transformers

### Common model initialization: application mitigation

[GHSA-29pf-2h5f-8g72](https://github.com/advisories/GHSA-29pf-2h5f-8g72) describes execution of remote attention-kernel code through untrusted configuration, bypassing expected remote-code consent. Unlike an unused architecture-specific feature, common model initialization is relevant to the Danish Wav2Vec2 alignment loader.

Recap now uses a custom Danish loader in `src/Recap/LocalWhisper/worker.py` with these constraints:

- Explicit `Wav2Vec2Config`, `Wav2Vec2Processor` and `Wav2Vec2ForCTC` classes rather than an arbitrary AutoModel architecture.
- Fixed Danish model revision `7a60985d596e7a9e26b01a0707fe59daac6ffe84`.
- After configuration parsing, force `config._attn_implementation = "eager"` so serialized internal attention settings cannot retain a repository-based kernel selection.
- Reject a non-null `quantization_config` rather than permitting an alternate quantized loading path.
- Pass `attn_implementation="eager"`, `use_kernels=False`, `weights_only=True` and `trust_remote_code=False` when loading the model.

A CPU test using the installed Transformers runtime and a mocked malicious configuration passed: the hardened path made no kernel-loading calls. This is targeted evidence for this mitigation, not proof against every malicious model configuration or all deserialization risks. `trust_remote_code=False` alone is not the mitigation for an advisory that bypasses that mechanism. The English aligner uses the fixed torchaudio Wav2Vec2 bundle instead.

### Other reviewed Transformers advisories

| Advisory | Relevant constraint in the current application |
| --- | --- |
| [GHSA-x9r9-c232-4q39](https://github.com/advisories/GHSA-x9r9-c232-4q39): custom-generation code downloaded before consent | Recap does not call the Transformers custom-generation loader/GenerationMixin custom-generate path. Speech generation uses CTranslate2 through faster-whisper; Danish alignment uses Wav2Vec2ForCTC. |
| [GHSA-xrqw-3rrv-vx5w](https://github.com/advisories/GHSA-xrqw-3rrv-vx5w): `save_pretrained` chat-template path traversal | No tokenizer/processor `save_pretrained` or chat-template export call was identified in the reviewed app inference flow. Loading a processor is distinct from invoking this affected export API. |
| [GHSA-fgcw-684q-jj6r](https://github.com/advisories/GHSA-fgcw-684q-jj6r): LightGlue nested remote-code trust bypass | Recap uses explicit Wav2Vec2 classes, not LightGlue or its nested configuration-loading path. |
| [GHSA-69w3-r845-3855](https://github.com/advisories/GHSA-69w3-r845-3855): Trainer RNG checkpoint loading | Recap does not train models or resume Trainer checkpoints. The installed CPU runtime also uses PyTorch 2.8, rather than the pre-2.6 PyTorch combination described in the advisory. This does not establish safety of unrelated checkpoint loaders. |

These are constraints on the current feature paths, not general non-applicability claims for the installed Transformers package.

## NLTK model-artifact path-security bypass

[GHSA-8mgp-746c-j5xp](https://github.com/advisories/GHSA-8mgp-746c-j5xp) is high severity, affects the installed NLTK 3.10.3, and had no patched version in the reviewed alert. It concerns `TransitionParser.train`/`parse`, `AveragedPerceptron.save`/`load`, `PerceptronTagger.save_to_json` and `save_maxent_params` using unguarded file operations on caller-controlled model paths.

No calls to those APIs were identified in the inspected Recap/WhisperX/pyannote/Wav2Vec2 flow. The actual NLTK use is:

1. Setup downloads `punkt_tab` and checks the fixed English/Danish tokenizer data.
2. WhisperX `alignment.py` loads `tokenizers/punkt_tab/{language}.pickle` via `nltk.data.load`.
3. NLTK `data.py` redirects this to `switch_punkt`, then `PunktTokenizer.load_lang` and `load_punkt_params` in `tokenize/punkt.py`, reading tokenizer tables through `open_datafile`.

Installed NLTK has `pathsec.ENFORCE=True`; the finding must not be dismissed on the assumption that enforcement is disabled. The limiting factors are the fixed supported languages and absence of an application feature accepting arbitrary NLTK model import/export paths. Setting `NLTK_DATA` does not itself provide OS-level filesystem containment.

## PyTorch memory-corruption advisories

The installed PyTorch 2.8.0+cpu falls within the affected ranges of all three reviewed advisories.

### JIT scripting

[GHSA-rrmf-rvhw-rf47](https://github.com/advisories/GHSA-rrmf-rvhw-rf47), low severity in the snapshot, concerns `torch.jit.script` (reported fix: 2.13.0).

No direct scripting call was identified in Recap's current eager WhisperX/pyannote/Wav2Vec2 inference path. Relevant adjacent code was checked:

- Torchaudio Wav2Vec2's `@torch.jit.export` is an annotation, not a request to compile the model.
- `asteroid_filterbanks/scripting.py` has `script_if_tracing` helpers that call `torch.jit.script` only while tracing; Recap does not request tracing.
- Lightning's `to_torchscript` export method calls scripting, but Recap does not export TorchScript models.
- Other installed Transformers architectures contain scripted helpers; they are not the selected Wav2Vec2 alignment architecture.

This source inspection does not exclude every indirect or future scripting path.

### LSTM cell

[GHSA-qfhq-4f3w-5fph](https://github.com/advisories/GHSA-qfhq-4f3w-5fph), low severity, concerns `torch.lstm_cell` (reported fix: 2.10.0). The [referenced patch](https://github.com/pytorch/pytorch/commit/999d94b5ede5f4ec111ba7dd144129e2c2725b03) adds gate-weight shape checks to native recurrent-cell entry points.

Pyannote's `PyanNet` and `SSeRiouSS` use `nn.LSTM`. Installed `torch/nn/modules/rnn.py` routes this through `_VF.lstm`; `nn.LSTMCell` instead uses `_VF.lstm_cell`. No direct affected cell API call was identified in the reviewed inference paths. Using LSTM layers therefore is not, by itself, evidence that this precise vulnerable entry point is called. Conversely, this distinction does not establish that all underlying recurrent kernels are free of defects.

### Packed-sequence unpacking

[GHSA-vgrw-7cvw-pwgx](https://github.com/advisories/GHSA-vgrw-7cvw-pwgx), medium severity, concerns `unpack_sequence` (reported fix: 2.9.1). The [referenced patch](https://github.com/pytorch/pytorch/commit/494518046816d29099b7d056a74ffa5c244fdcdd) adds an empty-`batch_sizes` check in native `_pad_packed_sequence`. Checking only callers named `unpack_sequence` would miss the shared `pad_packed_sequence` path.

No packed-sequence padding/unpacking use was identified in the reviewed WhisperX/pyannote/Wav2Vec2 inference code. Installed TorchMetrics NISQA does use `pad_packed_sequence`, but Recap does not invoke that audio-quality metric. A function being installed or imported is not evidence that it executes during recognition.

## Upgrade constraints and follow-up

WhisperX 3.8.6 declares `torch~=2.8.0` and `huggingface-hub<1.0.0`. The patched PyTorch versions listed above are outside that Torch constraint. Transformers 5 requires Hugging Face Hub 1.x or newer, conflicting with the WhisperX Hub constraint. Consequently these are not straightforward compatible lock-file bumps; dependency upgrades require a coordinated stack update and regression testing, rather than silently overriding resolver constraints.

Maintainers should review this evidence before publication, monitor upstream fixes and compatible releases, and rerun the review when dependencies, model sources, architectures, loading paths, export/training features or runtime options change. In particular:

- Retain regression coverage for the Danish configuration hardening.
- Verify the installed CPU and CUDA dependency graphs separately after any upgrade.
- Treat downloaded model provenance and local cache integrity as separate concerns. Hash-pinned community-1 assets and offline recognition do not fix local memory-corruption or file-access flaws.
- Do not infer GPU validation from CPU tests, or complete transitive reachability from this targeted source review.

No blanket approval, upstream patch, or alert dismissal is implied by this document.
