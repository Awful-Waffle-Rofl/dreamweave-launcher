# Third-party notices

## Redistributed in the exe

### Costura.Fody 6.2.0 (MIT)

Costura's runtime loader is injected into `DreamWeaveLauncher.exe` at build
time. License text from https://github.com/Fody/Costura (LICENSE):

```
The MIT License

Copyright (c) 2012 Simon Cropp and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## Build and test only (not shipped)

| Component | Version | License | Use |
| --- | --- | --- | --- |
| Fody | 6.9.3 | MIT (declared on nuget.org) | IL weaving host required by Costura.Fody |
| Microsoft.NETFramework.ReferenceAssemblies | 1.0.3 | MIT, per the package's licenseUrl (github.com/Microsoft/dotnet/blob/master/LICENSE) | Reference assemblies for building net48 |
| MSTest | 4.5.2 | MIT | Unit test framework |
| Microsoft.NET.Test.Sdk | 18.11.0 | MIT | Test host |

## To be added

- MegaApiClient (MIT) - for the mega.nz download.
- Newtonsoft.Json (MIT) - for manifest parsing.

Licenses for MSTest and Microsoft.NET.Test.Sdk have not been re-checked
against package metadata; confirm when each component's notice is finalized.
