﻿#region Copyright 2017 D-Haven.org

// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using DHaven.Faux.Compiler;
using DHaven.Faux.HttpSupport;

namespace DHaven.Faux
{
    public class Faux<TService>
        where TService : class // Really an interface
    {
        private TService service;

        public Faux()
        {
            TypeFactory.RegisterInterface<TService>();
        }

        /// <summary>
        /// Gets the global instance of that service for the application.
        /// </summary>
        public TService Service => service ?? (service = GenerateService(FauxConfiguration.Client));

        /// <summary>
        /// Usually called by tests, it generates a new instance every time, using the IHttpClient provided.
        /// </summary>
        /// <param name="client">client</param>
        /// <returns>the service</returns>
        public TService GenerateService(IHttpClient client)
        {
            return TypeFactory.CreateInstance<TService>(client);
        }
    }
}