using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.Reflection;

namespace SrvKit.Net
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class AutoRegisterGrpcAttribute : Attribute
    {
    }

    public static class RegistrationExtensions
    {
        public static void MapGrpcControllers(this WebApplication app, Assembly assembly = null)
        {
            assembly ??= Assembly.GetEntryAssembly();

            var grpcServiceTypes = assembly
                .GetTypes()
                .Where(t =>
                    t.IsClass &&
                    !t.IsAbstract &&
                    t.GetCustomAttribute<AutoRegisterGrpcAttribute>() != null);

            foreach (var type in grpcServiceTypes)
            {
                var method = typeof(GrpcEndpointRouteBuilderExtensions)
                    .GetMethods()
                    .First(m => m.Name == "MapGrpcService" &&
                                m.IsGenericMethod &&
                                m.GetParameters().Length == 1);

                var genericMethod = method.MakeGenericMethod(type);
                genericMethod.Invoke(null, new object[] { app });
            }
        }

        public static void MapRestControllers(this IEndpointRouteBuilder app)
        {
            app.MapControllers().RequireAuthorization();
        }
    }


}
