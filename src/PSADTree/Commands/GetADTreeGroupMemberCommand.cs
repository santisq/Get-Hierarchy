using System;
using System.DirectoryServices.AccountManagement;
using System.Management.Automation;

namespace PSADTree;

[Cmdlet(VerbsCommon.Get, "ADTreeGroupMember", DefaultParameterSetName = DepthParameterSet)]
[Alias("treegroupmember")]
[OutputType(
    typeof(TreeGroup),
    typeof(TreeUser),
    typeof(TreeComputer))]
public sealed class GetADTreeGroupMemberCommand : ADTreeCmdletBase
{
    [Parameter]
    public SwitchParameter ShowAll { get; set; }

    [Parameter]
    public SwitchParameter Group { get; set; }

    protected override void ProcessRecord()
    {
        Dbg.Assert(Identity is not null);
        Dbg.Assert(_context is not null);

        try
        {
            using GroupPrincipal? group = GroupPrincipal.FindByIdentity(_context, Identity);
            if (group is null)
            {
                WriteError(new ErrorRecord(
                    new NoMatchingPrincipalException($"Cannot find an object with identity: '{Identity}'."),
                    "IdentityNotFound",
                    ErrorCategory.ObjectNotFound,
                    Identity));

                return;
            }

            WriteObject(
                sendToPipeline: Traverse(
                    groupPrincipal: group,
                    source: group.DistinguishedName),
                enumerateCollection: true);
        }
        catch (Exception e) when (e is PipelineStoppedException or FlowControlException)
        {
            throw;
        }
        catch (MultipleMatchesException e)
        {
            WriteError(new ErrorRecord(
                e,
                "AmbiguousIdentity",
                ErrorCategory.InvalidResult,
                Identity));
        }
        catch (Exception e)
        {
            WriteError(new ErrorRecord(
                e,
                "Unspecified",
                ErrorCategory.NotSpecified,
                Identity));
        }
    }

    private TreeObjectBase[] Traverse(
        GroupPrincipal groupPrincipal,
        string source)
    {
        int depth;
        _index.Clear();
        _cache.Clear();
        _stack.Push((groupPrincipal, new TreeGroup(source, groupPrincipal)));

        while (_stack.Count > 0)
        {
            (GroupPrincipal? current, TreeGroup treeGroup) = _stack.Pop();

            try
            {
                depth = treeGroup.Depth + 1;

                // if this node has been already processed
                if (!_cache.TryAdd(treeGroup))
                {
                    current?.Dispose();
                    treeGroup.Hook(_cache);
                    _index.Add(treeGroup);

                    // if it's a circular reference, go next
                    if (TreeCache.IsCircular(treeGroup))
                    {
                        treeGroup.SetCircularNested();
                        treeGroup.Hierarchy = string.Concat(
                            treeGroup.Hierarchy.Insert(
                                treeGroup.Hierarchy.IndexOf("─ ") + 2,
                                _vtBrightRed),
                            _isCircular,
                            _vtReset);
                        continue;
                    }

                    // else, if we want to show all nodes
                    if (ShowAll.IsPresent)
                    {
                        // reconstruct the output without querying AD again
                        EnumerateMembers(treeGroup, depth);
                        continue;
                    }

                    // else, just skip this reference and go next
                    treeGroup.Hierarchy = string.Concat(
                        treeGroup.Hierarchy,
                        _isProcessed);
                    continue;
                }

                using PrincipalSearchResult<Principal>? search = current?.GetMembers();

                if (search is not null)
                {
                    EnumerateMembers(treeGroup, search, source, depth);
                }

                _index.Add(treeGroup);
                _index.TryAddPrincipals();
                current?.Dispose();
            }
            catch (Exception e) when (e is PipelineStoppedException or FlowControlException)
            {
                throw;
            }
            catch (Exception e)
            {
                WriteError(new ErrorRecord(
                    e, "EnumerationError", ErrorCategory.NotSpecified, current));
            }
        }

        return _index.GetTree();
    }

    private void EnumerateMembers(
        TreeGroup parent,
        PrincipalSearchResult<Principal> searchResult,
        string source,
        int depth)
    {
        foreach (Principal member in searchResult)
        {
            IDisposable? disposable = null;
            try
            {
                if (member is { DistinguishedName: null })
                {
                    disposable = member;
                    continue;
                }

                if (member is not GroupPrincipal)
                {
                    disposable = member;

                    if (Group.IsPresent)
                    {
                        continue;
                    }
                }

                TreeObjectBase treeObject = ProcessPrincipal(
                    principal: member,
                    parent: parent,
                    source: source,
                    depth: depth);

                if (ShowAll.IsPresent)
                {
                    parent.AddMember(treeObject);
                }
            }
            finally
            {
                disposable?.Dispose();
            }
        }
    }

    private TreeObjectBase ProcessPrincipal(
        Principal principal,
        TreeGroup parent,
        string source,
        int depth)
    {
        return principal switch
        {
            UserPrincipal user => AddTreeObject(new TreeUser(source, parent, user, depth)),
            ComputerPrincipal computer => AddTreeObject(new TreeComputer(source, parent, computer, depth)),
            GroupPrincipal group => HandleGroup(parent, group, source, depth),
            _ => throw new ArgumentOutOfRangeException(nameof(principal)),
        };

        TreeObjectBase AddTreeObject(TreeObjectBase obj)
        {
            _index.AddPrincipal(obj);
            return obj;
        }

        TreeObjectBase HandleGroup(
            TreeGroup parent,
            GroupPrincipal group,
            string source,
            int depth)
        {
            if (_cache.TryGet(group.DistinguishedName, out TreeGroup? treeGroup))
            {
                Push(group, (TreeGroup)treeGroup.Clone(parent, depth));
                return treeGroup;
            }

            treeGroup = new(source, parent, group, depth);
            Push(group, treeGroup);
            return treeGroup;
        }
    }

    private void EnumerateMembers(TreeGroup parent, int depth)
    {
        foreach (TreeObjectBase member in parent.Members)
        {
            if (member is not TreeGroup group)
            {
                _index.Add(member.Clone(parent, depth));
                continue;
            }

            Push(null, (TreeGroup)group.Clone(parent, depth));
        }
    }
}
