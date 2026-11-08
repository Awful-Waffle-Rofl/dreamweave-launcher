# Third-party notices

## Shipped beside the exe

### Newtonsoft.Json 13.0.4 (MIT)

Ships as `Newtonsoft.Json.dll`, an ordinary file next to `DreamWeaveLauncher.exe` (manifest and state parsing).
License text from https://github.com/JamesNK/Newtonsoft.Json (LICENSE.md):

```
The MIT License (MIT)

Copyright (c) 2007 James Newton-King

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Credited (data used by an optional feature)

### eriknihlen/ac-eor-palette-leak-fix (MIT)

The optional, default-off palette memory-leak fix uses this project's patch
data: two file offsets in the End-of-Retail `acclient.exe`, the 3 original bytes
at each, and the expected input and output SHA-256 values. The data is carried in
the signed manifest. No code from the project is included, and no patched binary
is distributed: the patch is applied on the player's machine only if they opt in.
License text from https://github.com/eriknihlen/ac-eor-palette-leak-fix (LICENSE):

```
MIT License

Copyright (c) 2026

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

This license applies only to the patcher script and accompanying documentation
in this repository. The patched binary it produces is a derivative of the
Turbine/Standing Stone Games acclient.exe and inherits its copyright; this
repository does not redistribute the binary.
```

## Build and test only (not shipped)

| Component | Version | License | Use |
| --- | --- | --- | --- |
| Microsoft.NETFramework.ReferenceAssemblies | 1.0.3 | MIT, per the package's licenseUrl (github.com/Microsoft/dotnet/blob/master/LICENSE) | Reference assemblies for building net48 |
| MSTest | 4.5.2 | MIT | Unit test framework |
| Microsoft.NET.Test.Sdk | 18.11.0 | MIT | Test host |

Licenses for MSTest and Microsoft.NET.Test.Sdk have not been re-checked
against package metadata; confirm when each component's notice is finalized.
