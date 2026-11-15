# Containers, Linux isolation and FinSim

This guide explains how containers work from the Linux kernel upward. It is
written for someone who wants to understand what Podman is doing, not only
which command to type. The second half maps those concepts to the production
Slurm deployment in this repository.

The short version is:

```text
container = ordinary Linux processes
            + a controlled view of the system
            + resource accounting and limits
            + filesystem and network setup
            + security policy
```

A container is not a small virtual machine. It normally does not have its own
kernel. Its processes use the host kernel, while Linux namespaces, cgroups,
capabilities, seccomp, security modules and mounts shape what those processes
can see and do.

For the production deployment contract, also read
[PRODUCTION.md](PRODUCTION.md). This guide explains the concepts behind that
contract; it does not replace its deployment gates.

## 1. The core mental model

### Image, container and process

An **image** is a packaged, mostly read-only filesystem plus metadata. It can
contain:

- an operating-system userspace;
- libraries and executables;
- configuration defaults;
- environment variables;
- an entrypoint and default command;
- health-check metadata.

A **container** is an instance of an image with a writable layer, namespaces,
mounts, networking and processes attached to it. The same image can create
many containers with different names, roles, volumes and environment values.

The actual application is still a normal Linux process. The container engine
does not turn the application into a special kind of executable.

```text
image:     reusable template
container: configured instance
process:   the program actually running
```

### Containerfile and build context

A [Containerfile](../Containerfile) is a build recipe. Instructions such as
`FROM`, `RUN`, `COPY`, `USER`, `ENTRYPOINT`, `CMD` and `HEALTHCHECK` describe
the image and its default runtime metadata.

`podman build` also receives a **build context**, usually the final `.` in:

```bash
podman build -f Containerfile .
```

The context is the set of files available to `COPY` and `ADD`. It is not the
same thing as the eventual container filesystem. A `.containerignore` file can
exclude files from the context.

Build-time and run-time are different phases:

| Phase | Typical actions | Where they occur |
| --- | --- | --- |
| Build time | Pull base image, install packages, compile code, copy files | Temporary build containers and image layers |
| Run time | Start entrypoint, mount volumes, join networks, run services | A container created from the image |
| Monitoring | Run health checks, collect logs, record exit status | Podman/conmon and short-lived processes inside the container |

`RUN` executes during the build. `ENTRYPOINT`, `CMD` and `HEALTHCHECK` describe
what happens when an image is run. A `HEALTHCHECK` is recorded as metadata; it
is not executed during `podman build`.

### Image layers

Images are assembled from layers. A base image supplies lower layers and
build instructions add more layers. A running container normally adds a
writable upper layer:

```text
read-only base image layers
        +
read-only application layers
        +
writable container layer
        ↓
merged filesystem visible to the process
```

Layers make reuse and caching possible. They are not a replacement for
persistent storage. Data that must survive container replacement belongs in a
volume or an explicit host mount.

## 2. Podman and the container stack

Podman is a container engine and management CLI. It builds, pulls, tags,
creates, starts, stops, inspects and removes images and containers. Podman is
daemonless in its usual Linux mode and can operate as a normal user. Its
command-line model is compatible with many Docker workflows, but its security
and storage configuration still come from the local host.

The important components are:

```text
podman CLI / libpod
        ↓
Buildah for image builds       conmon for a running container
                                  ↓
                           crun or runc
                                  ↓
                         container process
```

### Podman CLI and Buildah

The `podman` command is the user-facing manager. Podman uses Buildah for image
construction, and Podman and Buildah share image storage. The build engine
creates temporary environments for `RUN` instructions and commits their
filesystem changes into image layers.

### OCI runtime: `crun` or `runc`

An OCI runtime is the lower-level program that turns a container specification
into Linux processes and namespaces. It configures the root filesystem,
namespaces, capabilities, cgroups and other process attributes, then starts
the container's initial process.

The OCI runtime is normally a launcher, not the long-lived application
manager. It usually exits after creating the container process.

### `conmon`

`conmon` is a small monitor for one container. It usually remains alive while
the container runs and:

- keeps the container's standard streams available;
- writes logs or forwards them to journald;
- supports attach and log operations;
- observes the container process;
- records the exit code and termination time.

The approximate host-side relationship is:

```text
podman command
    ↓
conmon
    ↓
container PID 1
```

The exact parentage can vary with systemd and runtime configuration, but the
important point is that the Podman CLI is not normally the long-lived parent.
When the container's main process exits, conmon records the result and then
normally exits too. The stopped container's metadata can remain for
`podman ps -a` and `podman inspect`.

### Container PID 1

The first process in the container's PID namespace is usually called
container PID 1. In this project it is normally the configured entrypoint:

```dockerfile
ENTRYPOINT ["/usr/local/sbin/cluster-entrypoint"]
```

PID 1 has special responsibilities. It must receive termination signals,
reap orphaned child processes and supervise or replace child daemons
appropriately. A shell wrapper that does not use `exec` can accidentally hide
signals from the real service.

### `ENTRYPOINT` versus `CMD`

`ENTRYPOINT` identifies the main executable. `CMD` supplies a default command
or default arguments. For example:

```dockerfile
ENTRYPOINT ["/usr/local/bin/server"]
CMD ["--production"]
```

normally runs:

```text
/usr/local/bin/server --production
```

The `finsim-dev` stage resets the runtime entrypoint and sets
`CMD ["/bin/bash"]`, so it opens a developer shell rather than starting the
cluster entrypoint. See [Containerfile](../Containerfile).

### Health checks

`HEALTHCHECK` stores a command and timing policy in image metadata. While a
container is running, Podman periodically executes the check as a short-lived
process inside the container:

```text
container starts
    ↓
health state: starting
    ↓
Podman runs health command
    ↓
exit 0 → healthy
non-zero → failed attempt
    ↓
enough failures → unhealthy
```

It is not a cron job or systemd timer inside the container. It does not start
the main service and does not automatically restart a container unless a
configured failure action or external supervisor does so.

## 3. Linux namespaces: what a process can see

A namespace gives a process a separate view of a particular system resource.
Two processes can run on the same kernel while receiving different answers to
questions such as “which processes exist?” or “which network interfaces exist?”

Namespaces are about **visibility and identity**, not primarily resource
limits.

### PID namespace

A PID namespace gives a process its own process-numbering view.

Inside a container:

```text
PID 1  cluster-entrypoint
PID 8  slurmd
PID 20 job process
```

On the host, those same processes have different host PIDs. A process in the
container normally cannot see unrelated host processes through its PID view.
The host can still see container processes.

PID namespace isolation does not automatically guarantee correct signal
handling or cleanup. That is why the container's PID 1 and entrypoint design
matter.

### Mount namespace

A mount namespace gives a process a separate set of filesystem mounts. It is
the basis for showing a container its own root filesystem and hiding most host
mounts.

A mount namespace does not mean the container has a completely independent
filesystem. Host paths can be deliberately mounted into it as bind mounts or
volumes. The security result depends heavily on which paths are mounted,
whether they are read-only, and who owns the files.

### Network namespace

A network namespace gives a process its own network devices, addresses, routes
and port space. Podman can connect it to a bridge, macvlan, ipvlan, host
network or another network arrangement.

Publishing a port is a host-to-container forwarding rule, for example:

```bash
podman run -p 6817:6817 image
```

This mapping works even when the image has no `EXPOSE 6817`. `EXPOSE` is image
metadata; `-p` creates the explicit forwarding rule. The application must
still listen on the container port, normally on `0.0.0.0` rather than only on
loopback.

### User namespace

A user namespace maps user and group IDs between the container and host.
Inside a rootless container, UID 0 may appear to be `root`, but it maps to the
ordinary invoking user or a subordinate UID range on the host.

```text
inside container: UID 0 (root)
outside host:     unprivileged mapped UID
```

This is the core of rootless isolation. It also explains why rootless
containers have difficulty performing host-level cgroup, device and mount
operations.

### UTS namespace

The UTS namespace controls the hostname and domain name seen by the process.
Container-specific hostnames are useful for service discovery and logs without
changing the host's hostname.

### IPC namespace

The IPC namespace isolates System V IPC objects, POSIX message queues and
related inter-process communication resources. It prevents unrelated
containers from automatically sharing those objects.

### Cgroup namespace

A cgroup namespace controls how a process sees its cgroup hierarchy. It is
different from creating or managing cgroups.

```text
--cgroupns=private
    container sees a private cgroup namespace

--cgroupns=host
    container sees the host cgroup namespace
```

`--cgroupns=host` does not expose the host PID, network or mount namespaces.
It exposes the host cgroup namespace only, and permissions still determine
whether the process can change anything.

### Time namespace

Some Linux configurations can give processes a separate view of certain
clocks. It is less commonly relevant to ordinary application containers but
is another example of the kernel separating a process's view from the host.

### Namespace summary

| Namespace | Main question it changes | FinSim relevance |
| --- | --- | --- |
| PID | Which processes can I see? | Container PID 1, service supervision, job process visibility |
| Mount | Which mounts and filesystem tree can I see? | Read-only image, `/run` tmpfs, secrets, application and state mounts |
| Network | Which interfaces, routes and ports can I see? | Control-plane endpoints, routable production network, port isolation |
| User | Which UID/GID is this process really? | Rootless simulation versus trusted production `slurmd` |
| UTS | What hostname do I see? | Stable role and node names |
| IPC | Which IPC objects can I access? | Separation of unrelated services |
| Cgroup | Which cgroup hierarchy do I see? | Host cgroup access for production compute only |

## 4. Cgroups: what a process can use

Cgroups, or control groups, group processes so the kernel can measure and
control their resource usage. They are about **resource accounting and
enforcement**, not visibility.

### What cgroups can control

Depending on the kernel, hierarchy and permissions, cgroup controllers can
manage:

- CPU time and CPU weights;
- memory limits and high-pressure behavior;
- process counts;
- block I/O weights or limits;
- device access;
- huge pages, PIDs and other resources.

A cgroup does not make CPU or memory disappear. It tells the kernel how the
process group is allowed to compete for those resources.

### Cgroup v1 and v2

Linux historically exposed several separate v1 hierarchies. Cgroup v2 uses a
unified hierarchy with controllers enabled from parent to child. Modern Slurm
production deployments generally prefer cgroup v2 because its hierarchy and
delegation model are more coherent.

The v2 hierarchy has important rules:

- controllers must be enabled by a parent before a child can use them;
- a cgroup distributes resources to descendants;
- delegation must be explicit and writable;
- systemd generally owns the top-level hierarchy and delegates subtrees.

This is why merely mounting `/sys/fs/cgroup` is not enough. The correct
namespace, controller availability, ownership and delegation must all line up.

### Container cgroups versus job cgroups

There are two relevant layers in FinSim:

```text
host/systemd cgroup
    └── Podman container cgroup
          └── Slurm job cgroup
                └── job steps and processes
```

Podman's `CgroupsMode=enabled` asks Podman to create and manage a cgroup for
the container. Slurm's `CgroupPlugin=cgroup/v2`, `task/cgroup` and
`proctrack/cgroup` use cgroups inside the compute environment to control and
track individual jobs.

These settings are related but not interchangeable:

```text
CgroupsMode=enabled
    Podman container-level cgroup management

--cgroupns=host
    which cgroup namespace the container sees

CgroupPlugin=cgroup/v2
    Slurm job-level cgroup implementation
```

### Cgroups and rootless containers

A rootless container may have a cgroup, especially when a user systemd
instance or delegated user slice is available. That does not mean it can
manage the host hierarchy or enforce arbitrary job limits.

Root inside a rootless user namespace is not host root. A rootless `slurmd`
can therefore run a simulation, but it cannot honestly claim production
device, memory, CPU and mount-namespace enforcement.

### `CgroupsMode=enabled` versus `--cgroupns=host`

These settings answer different questions:

```text
CgroupsMode=enabled
    Should Podman create/manage a cgroup for this container?

--cgroupns=host
    Should the container see the host cgroup namespace?
```

Production compute needs both, plus privilege and a writable delegated mount.
`--cgroupns=host` alone does not grant permission. `CgroupsMode=enabled` alone
does not expose the host hierarchy.

## 5. Linux capabilities: what privileged operations are allowed

Traditional Unix permissions distinguish users and file modes. Linux
capabilities split many powers traditionally associated with root into named
privileges.

Examples include:

- `CAP_CHOWN`: change file ownership;
- `CAP_DAC_OVERRIDE`: bypass some file permission checks;
- `CAP_SETUID` and `CAP_SETGID`: change process identity;
- `CAP_KILL`: signal processes outside ordinary ownership rules;
- `CAP_NET_ADMIN`: change networking configuration;
- `CAP_SYS_ADMIN`: a very broad collection of administrative operations;
- `CAP_SYS_PTRACE`: inspect or control other processes;
- `CAP_MKNOD`: create device nodes.

A container can run as UID 0 while having many capabilities removed. Conversely,
adding capabilities can give a process powers that ordinary file ownership
would not provide.

### Capability sets

Linux tracks capability sets such as permitted, effective, inheritable,
ambient and bounding sets. Container engines usually construct a reduced set
for the container. The exact result depends on rootful/rootless mode, image
metadata, engine defaults and explicit flags.

### `--privileged`

`--privileged` is not one capability. It is a broad relaxation of container
restrictions, commonly involving capabilities, devices, security profiles and
other isolation defaults. It should be treated as a trusted-host exception,
not as a convenient fix for an unexplained permission error.

FinSim gives this profile only to production compute roles because root
`slurmd` needs host integration for job cgroups, device policy and job mount
namespaces. The renderer explicitly prevents other roles from receiving it.

### Least privilege in FinSim

Non-compute production roles receive:

```text
NoNewPrivileges=true
DropCapability=all
```

Then the renderer adds only narrowly selected capabilities required for
startup and service identity transitions. This preserves the distinction
between a rootful Podman engine and a privileged container.

## 6. Seccomp: which system calls are permitted

System calls are the interface between a process and the kernel. Programs use
system calls for operations such as:

- opening files;
- creating processes;
- changing namespaces;
- creating sockets;
- changing memory mappings;
- using devices;
- changing credentials.

Seccomp can filter system calls, commonly with a BPF-based policy. A policy can
allow a call, deny it, return an error, kill the process, or log the attempt.

Seccomp is valuable because a process may have a vulnerability in an
application or library. Even if the attacker gains code execution, a seccomp
profile can remove kernel entry points that the process does not need.

Seccomp is not a complete sandbox:

- it filters system-call names and arguments, not high-level intentions;
- a permitted system call can still be dangerous with the wrong capabilities;
- policy must account for the actual application and kernel;
- disabling seccomp may be necessary for unusual runtimes, but increases risk.

In a production deployment, inspect the effective seccomp profile rather than
assuming that `--privileged` or rootless mode provides the entire security
policy. Seccomp works together with namespaces, capabilities, mounts and an
LSM.

## 7. SELinux and AppArmor: mandatory security policy

Linux Security Modules add policy beyond ordinary Unix permissions and
capabilities.

### SELinux

SELinux labels files and processes and applies type-enforcement rules. A file
can have correct Unix ownership and still be inaccessible because its SELinux
label does not permit the container process to use it.

Podman volume options often include an SELinux relabel suffix such as `:z` or
`:Z`:

```text
host-path:container-path:ro,z
```

The exact choice determines whether the content is shared between containers
or privately relabeled. Relabeling changes security metadata, not ordinary file
ownership.

### AppArmor

AppArmor applies path- and profile-based restrictions. It is common on Ubuntu
and related systems. A profile can deny operations that namespaces and Unix
permissions would otherwise permit.

### Why this matters

SELinux/AppArmor are host policy. They are not replaced by a Containerfile.
The same image can behave differently on Fedora, Ubuntu, WSL and a hardened
production host because the host security module and labels differ.

`--privileged` may relax some security policy behavior, depending on the
engine and host configuration. It should not be treated as a portable or
precise way to configure SELinux/AppArmor.

## 8. Filesystems, mounts and propagation

### Overlay filesystem

Container images are often presented through an overlay filesystem. Lower
directories provide image layers, while an upper directory stores changes for
the container. The merged result looks like one filesystem to the process.

Deleting a file from a lower layer may create a whiteout in the upper layer;
it does not rewrite the original image.

### Bind mounts and named volumes

A bind mount exposes an explicit host path:

```bash
podman run -v /host/data:/container/data:rw image
```

A named volume is managed by Podman:

```bash
podman volume create application-data
podman run -v application-data:/var/lib/application image
```

Mounts can bypass parts of the image filesystem boundary. A writable host
mount is therefore a major part of a container's authority. Production should
mount only required paths, prefer read-only mounts for code and secrets, and
test ownership and labels.

### Read-only roots and temporary filesystems

Making the image root read-only prevents runtime mutation of shipped files.
Services still need writable locations for PID files, sockets, caches and
temporary data, so production commonly supplies narrow tmpfs or state mounts.

FinSim uses explicit paths for configuration, credentials, releases, workflow
data, controller state, database state and Slurm spool state. This makes the
write boundary visible and makes replacement possible without losing state.

### Mount propagation

Mount propagation controls whether a mount created under one namespace becomes
visible in another:

```text
private: mounts do not propagate
shared:  mounts propagate in both directions
slave:   propagation is one-way
```

The host source mount must itself support the requested propagation. A warning
that `/` is not a shared mount is therefore about the host's mount setup, not
the image's `HEALTHCHECK`. Ordinary bind mounts may still work, while nested
mount or host-integrated workloads may fail.

## 9. Networking and ports

Container networking combines a network namespace, virtual interfaces,
routes, DNS and sometimes a user-space port forwarder.

`EXPOSE` is image metadata:

```dockerfile
EXPOSE 6817
```

It documents a container port and can be used by automatic publish options. It
does not publish a host port by itself.

An explicit mapping does not require `EXPOSE`:

```bash
podman run -p 6817:6817 image
```

It does require a process inside the container to listen on port 6817. In a
distributed production cluster, the network must also provide routable
addresses and service discovery; a local host-only bridge is not enough.

## 10. Rootless and rootful Podman

### Rootless

Rootless Podman runs as an ordinary user. It uses a user namespace and mapped
UID/GID ranges. Its advantages include a smaller host privilege boundary and
better safety for development.

Its limitations can include restricted access to:

- host cgroups;
- devices;
- low-numbered ports;
- mount operations;
- kernel networking features;
- arbitrary host UIDs;
- nested containers.

Rootless root is not host root.

### Rootful

Rootful Podman is managed with host-level privileges, usually by a system
service or systemd/Quadlet. Rootful does not automatically mean every
container is privileged. Capabilities, namespaces, mounts, seccomp and LSM
policy can still restrict the container.

FinSim's production Quadlet units are system-level rootful units. Non-compute
roles remain restricted; compute roles are deliberately privileged trusted
host agents. Local simulation is rootless-friendly and disables Slurm's
production cgroup enforcement.

## 11. Defense in depth

No single container mechanism provides complete isolation. The layers answer
different questions:

```text
namespaces       What can the process see?
cgroups          What resources can the process group use?
capabilities     Which privileged operations may it perform?
seccomp          Which kernel entry points may it call?
SELinux/AppArmor Which host policy rules apply?
mounts           Which host data and devices are exposed?
user mapping     Which host identity does UID 0 represent?
read-only roots  Which shipped files can it modify?
```

If a container has a writable host filesystem mount, a powerful capability set
or privileged device access, namespace isolation alone is not a sufficient
security argument. Conversely, cgroups alone do not hide host processes or
protect secrets.

Production design is therefore a combination of:

- immutable, digest-pinned image;
- role-filtered mounts and secrets;
- read-only image and application paths;
- least privilege for non-compute services;
- host-integrated privilege only for dedicated compute nodes;
- TLS, MUNGE, JWT and identity validation;
- cgroup v2 job isolation;
- systemd supervision, health checks, logs and recovery procedures.

## 12. Applying the concepts to FinSim

### Image stages

The [Containerfile](../Containerfile) has three stages:

```text
rpm-builder
    builds version-pinned RPMs
        ↓ COPY --from
finsim-runtime
    installs runtime packages and common role scripts
        ↓ FROM finsim-runtime
finsim-dev
    adds SDK/tools/source and starts as developer with Bash
```

`rpm-builder` is an internal build environment. `finsim-runtime` is the
published runtime image used by local and production services. `finsim-dev` is
a development and CI toolbox, not a production daemon image.

### Common runtime entrypoint

The repository-specific startup workflow is documented in the detailed
[`cluster-entrypoint.sh` guide](./scripts/cluster-entrypoint.md), including
its role dispatch, validation stages, dependency waits, supervision, and
shutdown behavior.

The runtime image contains:

```dockerfile
ENTRYPOINT ["/usr/local/sbin/cluster-entrypoint"]
HEALTHCHECK ... CMD ["/usr/local/sbin/cluster-healthcheck"]
```

The entrypoint is a small root bootstrap supervisor. It validates the selected
profile and role, stages secrets and configuration into runtime locations, and
starts the role's daemon. It then drops privileges for most services. `slurmd`
is the intentional exception because it must launch and control job
allocations.

The health check is separate. Podman runs it periodically while the container
is alive. It checks the selected role's process and readiness behavior; it is
not the parent of the daemon and it is not a scheduler for the service.

### Quadlet template

The common production template is
[deploy/quadlet/finsim-role.container.in](../deploy/quadlet/finsim-role.container.in).
It defines:

```ini
ReadOnly=true
ReadOnlyTmpfs=true
CgroupsMode=enabled
LogDriver=journald
HealthInterval=30s
HealthTimeout=10s
HealthRetries=3
Notify=healthy
PidsLimit=4096
Delegate=yes
```

The settings mean:

- `ReadOnly=true`: the image root is not a general writable workspace;
- `ReadOnlyTmpfs=true`: temporary writable paths are provided as tmpfs where
  needed;
- `CgroupsMode=enabled`: Podman creates/manages the container cgroup;
- `LogDriver=journald`: logs are owned by the host journal;
- health settings: systemd and Podman receive bounded readiness information;
- `PidsLimit=4096`: a fork bomb or process leak cannot consume unlimited PIDs;
- `Delegate=yes`: systemd delegates the service cgroup subtree so the intended
  manager can create child cgroups.

### Role-specific production generation

[render-production-unit.sh](../scripts/render-production-unit.sh) is the
allow-list that turns a role into a Quadlet unit. It accepts roles such as
`database`, `slurmdbd`, `controller`, `restd`, `compute-master`,
`compute-worker`, `proxy` and `cache`.

For non-compute roles it emits restrictive settings:

```text
NoNewPrivileges=true
DropCapability=all
```

It then adds only the small capabilities and mounts required by that role.

For compute roles it emits the trusted host-agent profile:

```text
PodmanArgs=--privileged --cgroupns=host
Volume=/sys/fs/cgroup:/sys/fs/cgroup:rw
```

This is not merely a performance setting. It changes the security boundary:
root `slurmd` can interact with host cgroup v2, device policy and job mount
namespaces. Compute hosts must be dedicated and the profile must never leak to
controller, database or application roles.

### Slurm production settings

[prepare-config.sh](../scripts/prepare-config.sh) selects different Slurm
settings by deployment profile.

Production uses:

```text
ProctrackType=proctrack/cgroup
TaskPlugin=task/cgroup,task/affinity
JobAcctGatherType=jobacct_gather/cgroup
CgroupPlugin=cgroup/v2
ConstrainCores=yes
ConstrainRAMSpace=yes
ConstrainSwapSpace=yes
ConstrainDevices=yes
```

The meaning is:

```text
slurmd sees host-integrated cgroup v2
    ↓
Slurm creates job and step cgroups
    ↓
job processes are tracked together
    ↓
CPU, memory, swap and devices follow allocation policy
```

The settings require the Podman and host contract to be correct. The
entrypoint's `require_production_compute_isolation` function checks for a
non-remapped user namespace, cgroup v2, the host cgroup namespace, a writable
delegated hierarchy and the ability to create/remove a probe child cgroup.
See [cluster-entrypoint.sh](../scripts/cluster-entrypoint.sh).

### Simulation settings

Simulation uses:

```text
ProctrackType=proctrack/linuxproc
TaskPlugin=task/none
JobAcctGatherType=jobacct_gather/linux
CgroupPlugin=disabled
```

This is deliberate. The simulation can run rootlessly and portably without
claiming production isolation. The entrypoint rejects a non-disabled cgroup
plugin in the rootless simulation profile. The simulation can validate Slurm
workflow behavior, but it cannot prove production cgroup or device isolation.

## 13. Production versus simulation

| Concept | Production | Simulation | Reason |
| --- | --- | --- | --- |
| Podman manager | Rootful system-level Quadlet | Rootless-friendly Compose | Production needs system supervision and host integration; simulation must be easy to run locally. |
| Compute user namespace | Not remapped | Rootless mapping allowed | Production `slurmd` must manage host-integrated cgroups; simulation avoids that claim. |
| Container cgroups | `CgroupsMode=enabled` | Local/rootless-compatible behavior | Production needs predictable container grouping and supervision. |
| Cgroup namespace | Host for compute roles | Private/local | Only production compute needs the host cgroup hierarchy. |
| Privileges | Compute only: privileged; others: dropped capabilities | No privileged compute profile | Least privilege outside the trusted compute boundary. |
| Slurm process tracking | `proctrack/cgroup` | `proctrack/linuxproc` | Production must contain complete job process trees. |
| Slurm task plugin | `task/cgroup,task/affinity` | `task/none` | Production enforces resource and affinity policy. |
| Slurm accounting | `job_acct_gather/cgroup` | `job_acct_gather/linux` | Production usage follows cgroup-contained jobs. |
| Slurm cgroup plugin | `cgroup/v2` | `disabled` | Rootless simulation cannot honestly provide host-level job isolation. |
| Device policy | Production cgroup device enforcement | Not claimed | Device control needs host integration. |
| Mount/job namespaces | Production `job_container/tmpfs` path | Not claimed | Production job containment requires privileged host-agent access. |
| Authentication | MUNGE plus production RS256 JWKS | MUNGE plus local development JWT | Production trust roots and issuer controls are stronger and durable. |
| TLS | Required for cross-host control and services | Local profile may omit production TLS | Production traffic crosses failure and trust domains. |
| Network | Named routable network, host networking rejected | Local Compose network | Production nodes need cross-host reachability and service discovery. |
| State | Dedicated and tested persistent storage | Git-ignored local `.runtime/` | Production must survive replacement and support recovery. |

The central difference is not that production runs a different Slurm binary. It
is that production makes stronger operational claims: a job is isolated and
accounted for, identities and credentials are trusted, state is recoverable,
and compute hosts deliberately integrate with the host kernel.

## 14. Inspecting a running container

Useful commands and what they answer:

```bash
# Images and containers
podman images
podman ps -a

# Image metadata: entrypoint, health check, exposed ports, user
podman image inspect IMAGE

# Container state, mounts, namespaces, health and exit code
podman inspect CONTAINER

# Processes visible through the container view
podman top CONTAINER

# Host-side monitor and runtime processes
ps -ef | grep -E 'conmon|crun|runc'

# Enter a running container
podman exec -it CONTAINER /bin/bash

# Run the configured health check immediately
podman healthcheck run CONTAINER
```

Inside a container, these can reveal the effective isolation:

```bash
cat /proc/1/status
cat /proc/1/cgroup
cat /proc/self/uid_map
readlink /proc/1/ns/pid
readlink /proc/1/ns/mnt
readlink /proc/1/ns/net
readlink /proc/1/ns/user
readlink /proc/1/ns/cgroup
mount
```

On a production compute host, also inspect:

```bash
stat -f -c '%T' /sys/fs/cgroup
cat /sys/fs/cgroup/cgroup.controllers
cat /proc/$(pidof slurmd)/cgroup
```

These do not prove the entire security model, but they help distinguish a
missing cgroup filesystem, a private cgroup namespace, an unmapped user
namespace and a permissions/delegation problem.

## 15. Troubleshooting by layer

When a containerized service fails, identify the layer before changing
privileges.

| Symptom | Likely layer | Questions |
| --- | --- | --- |
| File is missing | Build context or mount | Was it copied into the image? Is the host mount hiding it? |
| Permission denied | UID/GID, capability, SELinux/AppArmor or read-only mount | Which user owns the path? Is the path labeled? Is the required capability present? |
| Port unreachable | Network namespace, listener or port publishing | Is the process listening on the right address? Is the port published or routed? |
| `slurmd` cgroup error | Cgroup namespace, host hierarchy, delegation or rootless mode | Is cgroup v2 mounted? Is the container using host cgroup namespace? Can it create a delegated child? |
| Container is `unhealthy` | Health command or role readiness | Does the role process exist? Does the check have its config and socket? |
| Container exits immediately | PID 1, entrypoint validation or command override | What do `podman logs` and the exit code say? Was `ENTRYPOINT` overridden? |
| Host mount warning | Mount propagation | Is the source mount shared/slave as required? Is this an ordinary bind mount or a nested-mount workload? |

Do not solve every failure with `--privileged`. First determine whether the
failure is caused by a missing file, wrong identity, missing capability,
security label, cgroup delegation or network configuration. A broad privilege
increase can hide the real configuration error and create a production risk.

## 16. Glossary and rules of thumb

**Container**: A configured set of processes using Linux isolation and a
filesystem assembled from an image and mounts.

**Image**: A reusable filesystem and metadata template.

**Namespace**: A kernel mechanism that gives a process a separate view of a
resource such as PIDs, mounts, networks or users.

**Cgroup**: A kernel mechanism that groups processes for resource accounting
and control.

**Capability**: A named portion of the powers traditionally associated with
root.

**Seccomp**: A system-call filter.

**SELinux/AppArmor**: Host-enforced mandatory security policy systems.

**Rootless**: Podman and its processes run without host root privileges, using
user namespaces and UID/GID mappings.

**Rootful**: Podman is managed with host-level privileges. It does not imply
that every container is privileged.

**Privileged container**: A container with broad restrictions relaxed. Treat
it as a trusted host component.

**Quadlet**: A systemd-integrated way to describe Podman containers, pods,
networks and volumes.

**`conmon`**: The long-lived monitor and log/exit-status bridge for a running
container.

**Container PID 1**: The first process in the container's PID namespace,
normally the entrypoint.

Rules of thumb:

1. Namespaces control what a process can see; cgroups control what its process
   group can use.
2. Root inside a rootless container is not host root.
3. `CgroupsMode=enabled` and `--cgroupns=host` are different settings.
4. Podman cgroups and Slurm job cgroups are different layers.
5. `--privileged` is a trusted-host exception, not a normal fix.
6. A read-only image does not make writable host mounts safe automatically.
7. `EXPOSE` documents a port; `-p` publishes it.
8. `HEALTHCHECK` reports health; it does not start or supervise the main
   application by itself.
9. A simulation that disables cgroups must not be used as evidence of
   production job isolation.
10. Production compute containers are host-trusted agents; isolate them on
    dedicated nodes and treat compromise accordingly.
