# fd distribution

These are unmodified executables from [fd v10.3.0](https://github.com/sharkdp/fd/releases/tag/v10.3.0), dual-licensed under MIT or Apache-2.0; both license texts are included and copied to the output. The archive SHA-256 values below match the release asset digests on GitHub. The executable hashes document the extracted files.

| RID | Release archive (prefix `fd-v10.3.0-`) | Archive SHA-256 | Executable SHA-256 |
| --- | --- | --- | --- |
| win-x64 | `x86_64-pc-windows-msvc.zip` | `318aa2a6fa664325933e81fda60d523fff29444129e91ebf0726b5b3bcd8b059` | `fd3d4853da7a319a604e1cb03ede88cbf584edd12b89a0991871fb4d9cd3ba5b` |
| win-arm64 | `aarch64-pc-windows-msvc.zip` | `bf9b1e31bcac71c1e95d49c56f0d872f525b95d03854e94b1d4dd6786f825cc5` | `3d94e5c3f04f1dc4fc0f9836cabf9a087f7b712299fcf1751fdcd055cef408d3` |
| osx-x64 | `x86_64-apple-darwin.tar.gz` | `50d30f13fe3d5914b14c4fff5abcbd4d0cdab4b855970a6956f4f006c17117a3` | `e3936d70c47bf8439797aa2c6c1ddff868424ff6bc418fc8501e819a2d58ccad` |
| osx-arm64 | `aarch64-apple-darwin.tar.gz` | `0570263812089120bc2a5d84f9e65cd0c25e4a4d724c80075c357239c74ae904` | `14134aadba85ab2cfe4494d0f44253145f897b77a23fd4d7df2cb4929b53c786` |
| linux-x64 | `x86_64-unknown-linux-musl.tar.gz` | `2b6bfaae8c48f12050813c2ffe1884c61ea26e750d803df9c9114550a314cd14` | `9f48273b6c780a5f4f084ef30bc67d98cbd7d10c55c4605cf3a6ee29b741af87` |
| linux-arm64 | `aarch64-unknown-linux-musl.tar.gz` | `996b9b1366433b211cb3bbedba91c9dbce2431842144d925428ead0adf32020b` | `d0fc407937b8a8aec44f3a80b4a08219ba61dde234590358abb168b44478d493` |

`source/backend/Urd.Core.csproj` imports `fd.targets`; its content assets propagate to the server and test projects. Build and publish copy only the binary for `RuntimeIdentifier`, or the .NET SDK host RID when no runtime identifier is set, to `fd/<RID>/fd` (`fd.exe` on Windows) relative to the executable's base directory. `linux-musl-x64` and `linux-musl-arm64` select the corresponding Linux binary, kept at the canonical `linux-x64` or `linux-arm64` path used by the scanner. Unsupported RIDs have no packaged executable. Both license files are copied for every supported RID. A clean Windows server publish was checked for the single runnable binary and both notices.

The six executables occupy 21,423,096 bytes (about 20.4 MiB); each output now contains only its selected executable plus the licenses. Unix mount-qualified scanning supports Linux and macOS; unknown filesystems and unsupported platforms fail closed.

On Unix hosts the targets set executable permission after build and publish. The distributor must preserve the Unix executable's normal executable mode (0755) in the final package. A Windows-hosted cross-publish cannot set Unix permission bits, so restore or preserve that mode when packaging for the target Unix host. Single-file publishing keeps fd and its licenses as external files. These binaries are not automatically checked for integrity at runtime or during builds; verify the hashes when updating them.
