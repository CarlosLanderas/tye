using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.Tye.Hosting.Model
{
    public class IngressStatus : ReplicaStatus
    {
        public IngressStatus(Service service, string name) : base(service, name)
        {
        }

    }
}
