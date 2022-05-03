# Updater

You want to change your program to compile to a dll. This dll must have a class called `Program` with the following signatures defined:
- a constructor `public Program(Action requestStop)`,
- a method `public int Main(string[] args)`,
- a method `public void ConsoleCommandReceived(string command)` and
- a method `public void Stop()`.

You will also want to add a launch config to your actual project somewhere along the lines of
```json
{
  "profiles": {
    "Debug": {
      "commandName": "Executable",
      "executablePath": "./Rennorb.Updater.exe",
      "workingDirectory": "../bin/Debug/net6.0/"
    }
  }
}
```
to be able to start your project from visual studio, since its now a dll. Debugging should still work perfectly fine. 

You will want to create a user that only has access to the repository used for updates. This user will be used in the OAuth process to get access to the repo for the automatic update process. This wouldn't strictly be needed for public repos, but I haven't implemented anything for those yet. Maybe they work, maybe they don't.

### Configuration
found/generated in `config/updater.json`

| setting            | default value | format | short description |
|--------------------|---------------|--------|-------------------|
|`program_dll_path`  |`program.dll`  |`relative path` | the program to load |
|`github_repo_id`    |`owner/repo`   |`string (owner/repo)`| the repo to use for updates  |
|`client_id`         |`xxxxxxxxxxxxxxxx`|`string` | client id of the github user whos auth token is used |
|`signature_hash_algorithm`|`SHA512` |`valid hash algorithm name defined in System.Security.Cryptography.HashAlgorithmName`| which algo to use |
|`signature_padding` |`Pkcs1`        |`Pkcs1 or Pss`  | which padding mode to use |
|`public_key_path`   |`res/public.pem`|`relative path` | where to find the public key |

### Signing
Use `--sign` with the binary or the `Sign Binaries` target to sign binaries. It expects 
- `private.pem`
- `sign/binaries.zip`
- `sign/version.txt`

and will produce `sign/signature.bin`. All three of the files in `sign/` should be attached to a github release pointed to by the configuration file.
