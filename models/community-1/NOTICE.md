# Community-1 attribution and redistribution notice

This directory contains the **pyannote speaker-diarization-community-1** pipeline and its bundled model files, published by **pyannote**:

- Source and original model card: <https://huggingface.co/pyannote/speaker-diarization-community-1>
- Publisher: <https://www.pyannote.ai/>
- License declared in the upstream model card: **Creative Commons Attribution 4.0 International (CC BY 4.0)**.
- License: <https://creativecommons.org/licenses/by/4.0/>
- Full legal text: [`LICENSE-CC-BY-4.0.txt`](LICENSE-CC-BY-4.0.txt), obtained from <https://creativecommons.org/licenses/by/4.0/legalcode.txt>.

These third-party materials are not covered by Recap's MIT license. Recap does not claim authorship or endorsement by pyannote or the component authors. The upstream model cards are retained in this directory and its component directories.

## Component credits

- **Speaker segmentation:** Alexis Plaquet and Hervé Bredin, *Powerset multi-class cross entropy loss for neural speaker diarization*, INTERSPEECH 2023. See the citation in [`README.md`](README.md).
- **Speaker embedding:** the model is copied upstream from <https://huggingface.co/pyannote/wespeaker-voxceleb-resnet34-LM>. [`embedding/README.md`](embedding/README.md) explicitly identifies CC BY 4.0 through the VoxCeleb dataset license and credits Hongji Wang, Chengdong Liang, Shuai Wang, Zhengyang Chen, Binbin Zhang, Xu Xiang, Yanlei Deng and Yanmin Qian, *Wespeaker: A research and production oriented speaker embedding learning toolkit*, ICASSP 2023. Upstream toolkit: <https://github.com/wenet-e2e/wespeaker>; dataset: <https://mm.kaist.ac.kr/datasets/voxceleb/>.
- **PLDA and speaker clustering:** [`plda/README.md`](plda/README.md) credits the [BUT Speech@FIT group](https://speech.fit.vut.cz/) for training the PLDA model, and [Jiangyu Han](https://github.com/jyhan03) and [Petr Pálka](https://github.com/Selesnyan) for VBx integration into pyannote.audio. The root card cites Federico Landini, Ján Profant, Mireia Diez and Lukáš Burget, *Bayesian HMM clustering of x-vector sequences (VBx) in speaker diarization: theory, implementation and analysis on standard tasks*, Computer Speech & Language, 2022.
- The upstream card acknowledges GENCI and the Jean Zay supercomputer for training and tuning.

The PLDA README does not declare a separate component license, and no separate segmentation model card is supplied here. The bundle's CC BY 4.0 attribution is based on its upstream root model card; this notice does not invent additional component license grants or certify the provenance of training data.

## Recap changes and redistribution

Recap adds this notice and a copy of the CC BY 4.0 legal text for redistribution. Model weights and pipeline configuration are used as supplied; adding attribution or omitting download-cache metadata and Git LFS routing metadata does not retrain or modify the models.

When redistributing this bundle, retain the upstream model cards, this attribution notice and the license text (or otherwise provide the attribution and license information required by CC BY 4.0). Identify any further changes you make. Do not imply endorsement or impose additional legal or technological restrictions that prevent recipients from exercising the licensed rights. See the full license for the controlling terms, including its warranty disclaimer and limitation of liability.

CC BY 4.0 does not grant all possible rights, such as privacy, publicity or trademark rights. Upstream hosting/access conditions and the licenses of software used to load the models are separate matters. This notice covers attribution for these model files, not a license audit of the Python runtime or all transitive dependencies.
