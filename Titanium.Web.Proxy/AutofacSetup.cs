using Autofac;
using EndPointProxy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Titanium.Web.Proxy
{
    class AutofacSetup : Module
    {        
        public AutofacSetup()
        {         
        }

        protected override void Load(ContainerBuilder builder)
        {
            //builder.RegisterInstance(new CommandLineArgumentsProvider(_commandLineArgs));
            //builder.RegisterType<MainForm>().AsSelf().AsImplementedInterfaces().SingleInstance();
            builder.RegisterType<EndPointProxyRequest>().AsImplementedInterfaces().InstancePerRequest();
        }
    }
}
