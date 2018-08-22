﻿#region Copyright 2018 D-Haven.org
// 
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using DHaven.Faux.HttpSupport;
using Microsoft.Extensions.Logging;
using TypeInfo = System.Reflection.TypeInfo;

namespace DHaven.Faux.Compiler
{
    public static class WebServiceClassGenerator
    {
        private static readonly ILogger Logger;
        
        static WebServiceClassGenerator()
        {
            Logger = DiscoverySupport.LogFactory.CreateLogger(typeof(WebServiceClassGenerator));

            var faux = DiscoverySupport.Configuration.GetSection("faux");
            var debug = faux.GetSection("debug");

            OutputSourceFiles = Convert.ToBoolean(debug["outputSource"]);
            SourceFilePath = debug["sourcePath"];

            if (string.IsNullOrEmpty(SourceFilePath))
            {
                SourceFilePath = "./dhaven-faux";
            }

            if (!Directory.Exists(SourceFilePath))
            {
                Directory.CreateDirectory(SourceFilePath);
            }
        }

        public static string SourceFilePath { get; set; }

        // ReSharper disable once MemberCanBePrivate.Global
        public static bool OutputSourceFiles { get; set; }

        public static string RootNamespace { get; set; } = "DHaven.Feign.Wrapper";

        // ReSharper disable once MemberCanBePrivate.Global
        public static bool GenerateSealedClasses { get; set; } = true;

        public static string GenerateSource(TypeInfo typeInfo, out string fullClassName)
        {
            if (!typeInfo.IsInterface || !typeInfo.IsPublic)
            {
                throw new ArgumentException($"{typeInfo.FullName} must be a public interface");
            }

            if (typeInfo.IsGenericType)
            {
                throw new NotSupportedException($"Generic interfaces are not supported: {typeInfo.FullName}");
            }

            var className = typeInfo.FullName?.Replace(".", string.Empty);
            fullClassName = $"{RootNamespace}.{className}";

            using (Logger.BeginScope("Generator {0}:", className))
            {
                var serviceName = typeInfo.GetCustomAttribute<FauxClientAttribute>().Name;
                var baseRoute = typeInfo.GetCustomAttribute<RouteAttribute>()?.BaseRoute ?? string.Empty;
                var sealedString = GenerateSealedClasses ? "sealed" : string.Empty;

                Logger.LogTrace("Beginning to generate source");
                
                var classBuilder = new StringBuilder();
                classBuilder.AppendLine($"namespace {RootNamespace}");
                classBuilder.AppendLine("{");
                classBuilder.AppendLine("    // Generated by DHaven.Faux");
                classBuilder.AppendLine(
                    $"    public {sealedString} class {className} : DHaven.Faux.HttpSupport.DiscoveryAwareBase, {typeInfo.FullName}");
                classBuilder.AppendLine("    {");
                classBuilder.AppendLine($"        public {className}()");
                classBuilder.AppendLine($"            : base(\"{serviceName}\", \"{baseRoute}\") {{ }}");

                foreach (var method in typeInfo.GetMethods())
                {
                    BuildMethod(classBuilder, method);
                }

                classBuilder.AppendLine("    }");
                classBuilder.AppendLine("}");

                var sourceCode = classBuilder.ToString();
                
                Logger.LogTrace("Source generated");

                if (!OutputSourceFiles)
                {
                    return sourceCode;
                }

                var fullPath = Path.Combine(SourceFilePath, $"{className}.cs");
                try
                {
                    Logger.LogTrace("Writing source file: {0}", fullPath);
                    File.WriteAllText(fullPath, sourceCode, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not write the source code for {0}", fullPath);
                }

                return sourceCode;
            }
        }
 
        private static void BuildMethod(StringBuilder classBuilder, MethodInfo method)
        {
            var isAsyncCall = typeof(Task).IsAssignableFrom(method.ReturnType);
            var returnType = method.ReturnType;

            if(isAsyncCall && method.ReturnType.IsConstructedGenericType)
            {
                returnType = method.ReturnType.GetGenericArguments()[0];
            }

            var isVoid = returnType == typeof(void);

            // Write the method declaration

            classBuilder.Append("        public ");
            if (isAsyncCall)
            {
                classBuilder.Append("async ");
                classBuilder.Append(typeof(Task).FullName);

                if(!isVoid)
                {
                    classBuilder.Append($"<{ToCompilableName(returnType)}>");
                }
            }
            else
            {
                classBuilder.Append(isVoid ? "void" : ToCompilableName(returnType));
            }

            var attribute = method.GetCustomAttribute<HttpMethodAttribute>();

            classBuilder.Append($" {method.Name}(");
            classBuilder.Append(string.Join(", ", method.GetParameters().Select(p => $"{ToCompilableName(p.ParameterType, p.IsOut)} {p.Name}")));
            classBuilder.AppendLine(")");
            classBuilder.AppendLine("        {");
            classBuilder.AppendLine("            var 仮variables = new System.Collections.Generic.Dictionary<string,object>();");
            classBuilder.AppendLine("            var 仮reqParams = new System.Collections.Generic.Dictionary<string,string>();");

            var contentHeaders = new Dictionary<string, ParameterInfo>();
            var requestHeaders = new Dictionary<string, ParameterInfo>();
            var responseHeaders = new Dictionary<string, ParameterInfo>();
            ParameterInfo bodyParam = null;
            BodyAttribute bodyAttr = null;

            foreach (var parameter in method.GetParameters())
            {
                AttributeInterpreter.InterpretPathValue(parameter, classBuilder);

                AttributeInterpreter.InterpretRequestHeader(parameter, requestHeaders, contentHeaders);

                AttributeInterpreter.InterpretBodyParameter(parameter, ref bodyParam, ref bodyAttr);

                AttributeInterpreter.InterpretRequestParameter(parameter, classBuilder);

                AttributeInterpreter.InterpretResponseHeaderInParameters(parameter, isAsyncCall, ref responseHeaders);
            }

            classBuilder.AppendLine($"            var 仮request = CreateRequest({ToCompilableName(attribute.Method)}, \"{attribute.Path}\", 仮variables, 仮reqParams);");
            var hasContent = AttributeInterpreter.CreateContentObjectIfSpecified(bodyAttr, bodyParam, classBuilder);

            foreach (var entry in requestHeaders)
            {
                classBuilder.AppendLine($"            仮request.Headers.Add(\"{entry.Key}\", {entry.Value.Name}{(entry.Value.ParameterType.IsClass ? "?" : "")}.ToString());");
            }

            if (hasContent)
            {
                // when setting content we can apply the contentHeaders
                foreach (var entry in contentHeaders)
                {
                    classBuilder.AppendLine($"            仮content.Headers.Add(\"{entry.Key}\", {entry.Value.Name}{(entry.Value.ParameterType.IsClass ? "?" : "")}.ToString());");
                }

                classBuilder.AppendLine("            仮request.Content = 仮content;");
            }

            classBuilder.AppendLine(isAsyncCall
                ? "            var 仮response = await InvokeAsync(仮request);"
                : "            var 仮response = Invoke(仮request);");

            foreach (var entry in responseHeaders)
            {
                classBuilder.AppendLine($"            {entry.Value.Name} = GetHeaderValue<{ToCompilableName(entry.Value.ParameterType)}>(仮response, \"{entry.Key}\");");
            }

            if (!isVoid)
            {
                var returnBodyAttribute = method.ReturnParameter?.GetCustomAttribute<BodyAttribute>();
                var returnResponseAttribute = method.ReturnParameter?.GetCustomAttribute<ResponseHeaderAttribute>();

                if (returnResponseAttribute != null && returnBodyAttribute != null)
                {
                    throw new WebServiceCompileException($"Cannot have different types of response attributes.  You had [{string.Join(", ", "Body", "ResponseHeader")}]");
                }

                if (returnResponseAttribute != null)
                {
                    AttributeInterpreter.ReturnResponseHeader(returnResponseAttribute, returnType, classBuilder);
                }
                else
                {
                    if (returnBodyAttribute == null)
                    {
                        returnBodyAttribute = new BodyAttribute();
                    }

                    AttributeInterpreter.ReturnContentObject(returnBodyAttribute, returnType, isAsyncCall, classBuilder);
                }
            }

            classBuilder.AppendLine("        }");
        }

        private static string ToCompilableName(HttpMethod method)
        {
            var value = method.Method.Substring(0, 1) + method.Method.Substring(1).ToLower();
            return $"System.Net.Http.HttpMethod.{value}";
        }

        private static string ToCompilableName(Type type, bool isOut)
        {
            var name = ToCompilableName(type);

            return !isOut ? name : $"out {name}";
        }

        internal static string ToCompilableName(Type type)
        {
            var baseName = type.FullName;
            Debug.Assert(baseName != null, nameof(baseName) + " != null");
            
            // If we have a ref or an out parameter, then Type.Name appends '&' to the end.
            if (baseName.EndsWith("&"))
            {
                baseName = baseName.Substring(0, baseName.Length - 1);
            }

            if (!type.IsConstructedGenericType)
            {
                return baseName;
            }

            baseName = baseName.Substring(0, baseName.IndexOf('`'));
            return $"{baseName}<{string.Join(",", type.GetGenericArguments().Select(ToCompilableName))}>";
        }        
    }
}