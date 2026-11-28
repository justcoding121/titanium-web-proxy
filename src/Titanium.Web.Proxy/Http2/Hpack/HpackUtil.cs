/*
 * Copyright 2014 Twitter, Inc
 * This file is a derivative work modified by Ringo Leese
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace Titanium.Web.Proxy.Http2.Hpack;

internal static class HpackUtil
{
    // Section 6.2. Literal Header Field Representation
    public enum IndexType
    {
        Incremental, // Section 6.2.1. Literal Header Field with Incremental Indexing
        None, // Section 6.2.2. Literal Header Field without Indexing
        Never // Section 6.2.3. Literal Header Field never Indexed
    }
}
