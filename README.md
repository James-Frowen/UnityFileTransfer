# UnityFileTransfer
Helper scripts to send large filers with Mirror or Mirage networking

**Look at Example files for how to use.**

Use `MaxKbPerSecond` to ensure that Transport send buffers to not become full. If send buffers are full the player will likely be disconnected. This important when sending files that are many Megabytes in size.

Supports: 

- [Mirage v139](https://github.com/MirageNet/Mirage/tree/v139.0.0) 
- [Mirror v78](https://github.com/MirrorNetworking/Mirror/tree/v78.0.3)

*may support over versions too, but the version above is what it has been testeed with*
