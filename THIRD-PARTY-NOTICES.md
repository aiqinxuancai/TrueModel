# ModelTrace 来源与许可

TrueModel 的 C# 挑战生成、接口适配和归因算法移植自
[xqy2006/ModelTrace](https://github.com/xqy2006/ModelTrace)。
参考文件：fingerprint.py、enrollment.py、static/challenge-browser.js。
参考提交：60949ef522a84f66b1236b459308b48028d36949。
TrueModel/Assets/unified_bank.json 保留上游 data/unified_bank.json 原始文件，
指纹库提交：55a2e4a55170423b484d701e9a82ab62b268c811，SHA-256：
6A678E6C73EB015C1C507D61CDD1313B41916D55186BB65920D1BF119EC5009E。

运行时为纯 C#，不包含或调用 Python；下列许可适用于移植部分与指纹库。

MIT License

Copyright (c) 2026 xqy2006

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

## 糖果检测参考来源

固定糖果题与独立数字 `21` 的判分规则参考
[haowang02/codex-candy-eval](https://github.com/haowang02/codex-candy-eval/blob/29127fa5a12fb7654e865f684dcaf55ade181349/codex_candy_eval.py)，
参考提交：29127fa5a12fb7654e865f684dcaf55ade181349。
TrueModel 使用自己的 C# 模型 API 调用，不包含或运行上游 Python/CLI 程序。
该参考提交未提供 LICENSE 文件；上文 ModelTrace 的 MIT 许可不适用于此来源。
