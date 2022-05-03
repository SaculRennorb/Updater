# Updater

This is supposed to live 
```
your_project
|-dock
| |-app
| \-docker-compose.yaml
|-bin
|-src
| |-your_dll
| | |-your_dll.csproj
| | \-...
| \-Rennorb.Updater   <------- HERE
|   |-Rennorb.Updater.csproj
|   \-...
\-your.sln
```
and the main project in your solution should be the Updater. It will automatically include other projects if this structure is used. There is also a profile to publish all the binaries and dependancies into `dock/app` if you want that.

### Signing
Use `--sign` with the binary or the `Sign Binaries` target to sign bnaries. It expects 
- `private.pem`
- `sign/binaries.zip`
- `sign/version.txt`

and will produce `sign/signature.bin`. All three of the files in `sign/` should be attached to a github release pointed to by the configurration file.

### Configuration
found/generated in `config/updater.json`

| setting            | default value | format | short description |
|--------------------|---------------|--------|-------------------|
|`program_dll_path`  |`program.dll`  |`relative path` | the program to load |
|`github_repo_id`    |`owner/repo`   |`string (owner/repo)`| the repo to use for updates  |
|`client_id`         |`xxxxxxxxxxxxxxxx`|`string` | client id of the github user whos auth token is used |
|`signature_hash_algorithm`|`SHA512` |`valid hash algorithm name defined in System.Security.Cryptography.HashAlgorithmName`| which algo to use |
|`signature_padding` |`Pkcs1`        |`Pkcs1|Pss`  | which padding mode to use |
|`public_key_path`   |`res/public.pem`|`relative path` | where to find the public key |