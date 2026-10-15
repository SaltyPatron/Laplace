Laplace authenticated API client

Extract the complete archive into a permanent directory. The runtime is included;
no .NET SDK, native substrate installation or database enrollment is required.
The manifest identifies the exact source revision and SHA-256 of every payload file.

Use an API key issued for your workspace. Keep the key in a private file outside
this package. The installers take the file path, never the key as an argument.
Installation checks the selected service's authenticated catalog before changing
the installed command. Existing unrelated commands are preserved.

Windows x64 (PowerShell 7), from the extracted directory:

  ./deploy/client/Install-LaplaceApiClient.ps1 -Origin https://api.hartonomous.com -KeyFile C:/Users/YOU/.laplace/api-key -Destination C:/Users/YOU/.laplace/client -CommandDirectory C:/Users/YOU/.local/bin

Linux x64 (Python 3.10 or newer), from the extracted directory:

  python3 deploy/client/install-api-client.py --origin https://api.hartonomous.com --key-file "$HOME/.laplace/api-key" --destination "$HOME/.laplace/client" --command-directory "$HOME/.local/bin"

Use absolute paths belonging to your account. On Linux, add the selected command
directory to PATH if the installer reports it missing. Keep the extracted package
in place: the installed profile points to its runtime directory.

Commands after installation:

  laplace-mono --where
  laplace-mono ops
  laplace-mono ingest document /absolute/path/to/documents --verify
  laplace-mono content get FILE_ID
  laplace-mono content export FILE_ID /absolute/path/to/new-file.txt
  laplace-mono chat "Explain this relationship"

Admission prints each file's receipt. --verify compares the returned bytes.
Export refuses to overwrite an existing file. A failed batch can have completed
earlier files; use their receipts before retrying. Chat retains the native response
receipt, including unresolved results. A successful transport is not proof of a
completed semantic answer.
