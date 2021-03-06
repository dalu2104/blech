module Blech.Visualization.BlechVisGraph

    open Blech.Common
    open Blech.Common.GenericGraph
    open System.Collections.Generic
    open Blech.Frontend.CommonTypes

    //____________________ General.____________________________
    /// Gives the xth element of a 4-tuple or triple.
    let frst3 (a,_,_)  = a
    let scnd3 (_,b,_)  = b
    let thrd3 (_,_,c)  = c
    let frst4 (a,_,_,_) = a
    let scnd4 (_,b,_,_) = b
    let thrd4 (_,_,c,_) = c  
    let frth4 (_,_,_,d) = d
    let frst5 (a,_,_,_,_) = a
    let scnd5 (_,b,_,_,_) = b
    let thrd5 (_,_,c,_,_) = c  
    let frth5 (_,_,_,d,_) = d
    let ffth5 (_,_,_,_,e) = e

    //____________________ Types.____________________________
    /// Specifies a (input or output parameter). First String specifies the type of the Parameter, second String specifies the name.
    type Param = { TypeName: string; Name: string}

    /// List of params. Might need some operations?                
    type ParamList = Param list

    /// Type for identifying sources or targets
    type SourceOrTarget = Source | Target
   
    /// Facadeing the complex expression, for short: Visgraph.
    type VisGraph = GenericGraph.Graph<NodePayload, EdgePayload>      

    /// Payload to enter in an activity graph.
    and ActivityPayload = {InputParams : ParamList; OutputParams : ParamList; LocalVars : string list}

    /// Determines if a node closes an if-else case.
    and StateCount = int

    /// Pair of ids: State Count and Secondary Id.
    and IdPair = StateCount * StateCount

    /// Optional id pair. Used for potential follow up nodes that need to be saved and identified.
    and IdPairOpt = {Opt :Option<IdPair>} with
        member x.UpdateSecondary i = if x.Opt.IsSome then {Opt = Some ((fst x.Opt.Value), i)} else {Opt = None}
        member x.StateCount = (fst x.Opt.Value)
        member x.SecondaryId = (snd x.Opt.Value)

    /// Payload to fill into a cobegin node. 
    and CobeginPayload = {Content : (VisGraph * Strength) list; CaseClosingNode : IdPairOpt} with
        member x.SetSecondaryIdOfCaseClosingNode i = {Content = x.Content; CaseClosingNode = x.CaseClosingNode.UpdateSecondary i}

    /// Shows if activity payload is present
    and IsActivity = IsActivity of ActivityPayload | IsNotActivity

    /// Run statement, calling an activity. In- and output variables.
    and IsActivityCall = {origName : string; iovars : (string list * string list)} with
        member x.GetIns = fst x.iovars
        member x.GetOuts = snd x.iovars

    /// Specifies a complex node to a specific abort type. The strings are the abort labels.
    and IsAbort = AbortWhen of string | AbortRepeat of string | WeakAbort | Neither
   
    /// Content of a complex node.
    and ComplexNode = {Body : VisGraph; IsActivity : IsActivity; CaseClosingNode : IdPairOpt; IsAbort : IsAbort} with
        member x.isWeakAbort = match x.IsAbort with WeakAbort -> true | _ -> false
        member x.SetSecondaryIdOfCaseClosingNode i = 
            {Body = x.Body; IsActivity = x.IsActivity; CaseClosingNode = x.CaseClosingNode.UpdateSecondary i; IsAbort = x.IsAbort}
        member private x.SetBody bdy = 
            {Body = bdy; IsActivity = x.IsActivity; CaseClosingNode = x.CaseClosingNode; IsAbort = x.IsAbort}
        member x.SetSecIdCaseClosingAndBody i body = (x.SetSecondaryIdOfCaseClosingNode i ).SetBody body
    
    /// Type to match whether a node is simple or complex or a cobegin node. Cobegin nodes are very different from others due to their concurrenc nature.
    /// IsActivityCall consists of the input and output variable names.
    and ComplexOrSimpleOrCobegin = IsComplex of ComplexNode | IsSimple | IsConnector | IsCobegin of CobeginPayload | IsActivityCall of IsActivityCall with
        member x.SetSecondaryIdOfCaseClosingNode i = 
            match x with 
                | IsComplex cmplx -> IsComplex (cmplx.SetSecondaryIdOfCaseClosingNode i)
                | IsCobegin cbgn-> IsCobegin (cbgn.SetSecondaryIdOfCaseClosingNode i)
                | _ -> x

    /// Determines if a node is an initial node.
    and IsInit = IsInit | IsNotInit

    /// Determines if a node is a final node.
    and IsFinal = IsFinal | IsNotFinal

    /// Determines whether something is "Initial" or "Final".
    and InitOrFinalOrNeither = {Init : IsInit; Final : IsFinal} with 
        member x.IsInitBool = match x.Init with IsInit -> true | IsNotInit -> false
        member x.IsFinalBool = match x.Final with IsFinal -> true | IsNotFinal -> false

    /// Indicating, if a node has been transformed to sctx (visualized) or not.
    and WasVisualized = Visualized | NotVisualized

    /// Indicating, whether outgoing edges of this node have been edge simplified.
    and WasEdgeSimplified = Simplified | NotSimplified

    /// Payload for a node.
    and NodePayload = { Label : string; 
                        IsComplex : ComplexOrSimpleOrCobegin ; 
                        IsInitOrFinal : InitOrFinalOrNeither; 
                        StateCount : StateCount;
                        SecondaryId : StateCount;
                        mutable WasVisualized : WasVisualized} with
        // TODO why are these two handled with mutable variables?
        member x.Visualize = x.WasVisualized <- Visualized
        member x.GetCobeginFromComplex = match x.IsComplex with IsCobegin cgbn -> Some cgbn | _ -> None
        // TODO rename these functions. Setting might be misleading, as a newly changed payload is copied.
        member x.SetSecondaryId i = {Label = x.Label; IsComplex = x.IsComplex.SetSecondaryIdOfCaseClosingNode i; IsInitOrFinal = x.IsInitOrFinal; StateCount = x.StateCount; SecondaryId = i; WasVisualized = NotVisualized}
        member x.SetComplex cmplx = {Label = x.Label; IsComplex = cmplx; IsInitOrFinal = x.IsInitOrFinal; StateCount = x.StateCount; SecondaryId = x.SecondaryId; WasVisualized = NotVisualized}
        member x.SetLabel i = {Label = i; IsComplex = x.IsComplex; IsInitOrFinal = x.IsInitOrFinal; StateCount = x.StateCount; SecondaryId = x.SecondaryId; WasVisualized = NotVisualized}
        member x.SetFinalStatusOn = {Label = x.Label; IsComplex = x.IsComplex; IsInitOrFinal = {Init = x.IsInitOrFinal.Init; Final = IsFinal}; StateCount = x.StateCount; SecondaryId = x.SecondaryId; WasVisualized = NotVisualized}
        member x.SetFinalStatusOff = {Label = x.Label; IsComplex = x.IsComplex; IsInitOrFinal = {Init = x.IsInitOrFinal.Init; Final = IsNotFinal}; StateCount = x.StateCount; SecondaryId = x.SecondaryId; WasVisualized = NotVisualized}
        member x.SetInitStatusOn = {Label = x.Label; IsComplex = x.IsComplex; IsInitOrFinal = {Init = IsInit; Final = x.IsInitOrFinal.Final}; StateCount = x.StateCount; SecondaryId = x.SecondaryId; WasVisualized = NotVisualized}
        member x.SetInitStatusOff = {Label = x.Label; IsComplex = x.IsComplex; IsInitOrFinal = {Init = IsNotInit; Final = x.IsInitOrFinal.Final}; StateCount = x.StateCount; SecondaryId = x.SecondaryId; WasVisualized = NotVisualized}
        member x.GetActivityOrigLabel = match x.IsComplex with | IsActivityCall call -> call.origName | _ -> ""

    /// Determines what kind of edge the edge ist.
    and EdgeProperty = IsAwait | IsTerminalAwait | IsConditional | IsImmediate | IsTerminal | IsAbort | IsConditionalTerminal

    /// Payload for an edge.
    and EdgePayload = {Label : string; Property : EdgeProperty; mutable WasSimplified : WasEdgeSimplified} with
        member x.CopyAsSimplified = {Label = x.Label ; Property = x.Property; WasSimplified = Simplified}
        member x.CopyAsNotSimplified = {Label = x.Label ; Property = x.Property; WasSimplified = NotSimplified}
        member x.CopyWithProperty prop = {Label = x.Label ; Property = prop; WasSimplified = x.WasSimplified}

    /// Node of a graph extracted from Blech code.
    and BlechNode = Node<NodePayload, EdgePayload>

    /// Edge of a graph extracted from Blech code.
    and BlechEdge = Edge<NodePayload, EdgePayload>

    /// Type for an edge accumulator. Edge String * Recursive Node Strings * edge priority count.
    and EdgeAccumulator = string * string * int

    /// Type for sequentially constructing the graph. Consists of: current graph, previous available node for connection and current state count (for distinct state identifiers.)
    /// Fourth element is a list of strings that contains the names of all parameters needed to make function calls in this scope. 
    /// Fourth element is used to compare to the list of defined in- and output variables to determine the missing variables that have to be defined.
    /// Fifth element is the last specified label in the code. Might be empty, if a label has been used or there isn't one present.
    /// Can later be used for implementation of actual local variables.
    /// TODO? why is this a tuple? 
    and GraphBuilder = VisGraph * (Option<BlechNode>) * int * string list * Option<string>

    /// Sets the label option of the given GraphBuilder to None.
    let setLabelOptionToNone (before : GraphBuilder) : GraphBuilder = (frst5 before, scnd5 before, thrd5 before, frth5 before, None)

    /// Sets the label option of the given GraphBuilder to the given string.
    let attachToLabelOption (before : GraphBuilder) (label : string) : GraphBuilder =
        let newLabel = match ffth5 before with
                        | Some value -> Some (value + " " + label)
                        | None -> Some label
        (frst5 before, scnd5 before, thrd5 before, frth5 before, newLabel)

    /// Returns, if the label option of the given GraphBuilder has a value.
    let hasLabel (gb : GraphBuilder) : bool = (ffth5 gb).IsSome
    
    /// Returns the value of the given GraphBuilder, if present. Returns empty string otherwise.
    let returnLabel (gb : GraphBuilder) : string = match hasLabel gb with true -> (ffth5 gb).Value | false -> ""

    let NeitherInitOrFinal = {Init = IsNotInit; Final = IsNotFinal}
    let InitNotFinal = {Init = IsInit; Final = IsNotFinal}
    let FinalNotInit = {Init = IsNotInit; Final = IsFinal}
    let InitAndFinal = {Init = IsInit; Final = IsFinal}

    //____________________ Unspecified helping methods.___________________________
    /// Returns the ids as a pair for a Blech node.
    let findIds = (fun (n:BlechNode) -> (n.Payload.StateCount, n.Payload.SecondaryId))

    /// Converts an edge to a int representation needed for the variable 'simplifiedEdges'.
    let convertToIdTuple (e : BlechEdge) = ((e.Source.Payload.StateCount, e.Source.Payload.SecondaryId), 
                                            (e.Target.Payload.StateCount, e.Target.Payload.SecondaryId))

    /// Matches two given nodes by their id.
    let matchNodes = fun (n1 : BlechNode) (n2:BlechNode) -> n1.Payload.StateCount = n2.Payload.StateCount && n1.Payload.SecondaryId = n2.Payload.SecondaryId

    //____________________ Find first await on every path in a graph.____________________________
    /// Function that determines whether a node (identified by its state count) is valid, because it is part of a list of valid ids.
    let private isValidNode (validNodeIdList : (int*int) list) = fun (n : BlechNode) -> List.contains (n.Payload.StateCount, n.Payload.SecondaryId) validNodeIdList
    /// Function that determines whether an edge (identified by state count of source and edge) is valid, because it is part of a list of valid ids.
    let private isValidTarget (validNodeIdList : (int*int) list) = fun (e : BlechEdge) -> List.contains (e.Target.Payload.StateCount, e.Target.Payload.SecondaryId) validNodeIdList

    /// Checks a list of edges, whether or not there is an await edges among them.
    let rec checkEdgesForAwait (edges: BlechEdge list) : bool =
        match edges with
            | head :: tail -> match head.Payload.Property with
                                | IsAwait -> true
                                | _ -> checkEdgesForAwait tail
            | [] -> false

    /// Checks if a node is stateful (not a simple state).
    let isActivityCallOrOtherComplex = 
        fun (n:BlechNode) -> 
            match n.Payload.IsComplex with
               | IsSimple | IsConnector _ -> false
               | _ -> true

    /// Checks if a node has an outgoing await edge to a valid target.
    let private isAwaitNode = fun (validNodes : (StateCount * StateCount) list) (n:BlechNode) -> 
        checkEdgesForAwait (List.filter (isValidTarget validNodes) (Seq.toList n.Outgoing))

    /// Starts the search of the first await on a path, given by a node starting the path. 
    /// The nodes that are allowed to be in the path are given by the list of state count integers.
    /// Successors of the nodes and its successors might be invalid and should not be considered, hence the list.
    /// Returns all valid nodes in the path that follow the first found await statement. Returned nodes have to be stateful.
    /// Second element is the actual first awaiting node of every path. Third element is the already checked nodes so far.
    /// If there is an await edge going out of the current node, we reached the first await.
    /// If the current node is an activity (call), it must contain an await, and is thus the first await statement.
    /// Hierarchies are broken from the inside out. Hence, if a complex is met, it is expected to have an await in it.
    let rec findFirstAwaitNodeOnEveryPath (entryPoint : BlechNode) 
                                          (validNodes : (StateCount * StateCount) list)
                                          (checkedNodes : (StateCount * StateCount) list)
                                          : BlechNode list * (Option<BlechNode> list) * (StateCount * StateCount) list =
        let isActivityCallOrOtherComplex = isActivityCallOrOtherComplex entryPoint
        let isAwaitEdge = isAwaitNode validNodes entryPoint

        let validAndNotYetChecked = fun n -> isValidNode validNodes n && not (isValidNode checkedNodes n)
        let validSuccessors = List.filter (validAndNotYetChecked) (Seq.toList entryPoint.Successors)

        let listAndChecked = match isAwaitEdge || isActivityCallOrOtherComplex with
                                | true -> (addAllSubsequentNodes validSuccessors validNodes [entryPoint] [entryPoint], [Some entryPoint], checkedNodes)
                                // Found first await, just add all subsequent nodes to the list.
                                | false -> checkNodesForAwaitsInPath validSuccessors validNodes checkedNodes
                                
        let distinctAndFilteredListOfValidNodes = if List.exists (fun (e:Option<_>) -> e.IsSome) (scnd3 listAndChecked) then
                                                    let distinctFollowUpList = List.distinct (frst3 listAndChecked)
                                                    let firstAwaits = (scnd3 listAndChecked)
                                                    // Filter the distinct follow up list from the nodes that are first await nodes.
                                                    // An element stays in the list if the element is NOT found in the list of first awaits.
                                                    let filterList = fun followUpNode -> not (List.exists (fun (firstAwait:Option<_>) -> if firstAwait.IsSome then matchNodes followUpNode firstAwait.Value else false) firstAwaits)
                                                    List.filter filterList distinctFollowUpList
                                                  else 
                                                    (List.distinct (frst3 listAndChecked))
        (distinctAndFilteredListOfValidNodes, scnd3 listAndChecked, thrd3 listAndChecked) 
    
    /// Recursively checks single nodes in a list of nodes for their first await.
    /// Returns: valid nodes after first await, first await node, and all checked nodes so far.
    and private checkNodesForAwaitsInPath (nodes : BlechNode list) 
                                          (validNodes : (int*int) list)
                                          (checkedNodes : (StateCount * StateCount) list)
                                          : BlechNode list * (Option<BlechNode> list) * (StateCount * StateCount) list =
        match nodes with
            | head :: tail -> let headChecked = findFirstAwaitNodeOnEveryPath head validNodes (findIds head::checkedNodes)
                              let tailChecked = checkNodesForAwaitsInPath tail validNodes (thrd3 headChecked)
                              (frst3 headChecked @ frst3 tailChecked, scnd3 headChecked @ scnd3 tailChecked , thrd3 tailChecked)
            | [] -> ([], [None] ,checkedNodes)

    /// Constructs a list of the given nodes and all subsequent nodes (that are valid) and stateful.
    /// Valid nodes are ones that were in the subgraph and have not been checked yet.
    /// Nodes to check do not need to be filtered, as they have already been checked beforehand.
    /// The accumulator accumulates the subsequent nodes.
    and private addAllSubsequentNodes (nodesToCheck : BlechNode list) (validNodes: (int*int) list) (checkedNodes : BlechNode list) (accumulator : BlechNode list): BlechNode list = 
        match nodesToCheck with
            | head :: tail -> // Determine valid successors to check.
                              let isNodeValid = fun (n:BlechNode) -> isValidNode validNodes n && not (isValidNode (List.map findIds checkedNodes) n)
                              let validSuccessors = List.filter isNodeValid (Seq.toList head.Successors)
                              // Current node is added to accumulator if it is stateful: is complex or has an outgoing await transition.
                              let updatedAcc = 
                                match isAwaitNode validNodes head || isActivityCallOrOtherComplex head with
                                    | true -> (head :: accumulator)
                                    | false -> accumulator

                              addAllSubsequentNodes (validSuccessors@tail) validNodes (head :: checkedNodes) updatedAcc
            | [] ->  accumulator

    //____________________________________Find specific nodes/edges in hashset/list.
    /// Finds the node that has matches true on the given function and returns it.
    let private findNodeInHashSet(nodes : HashSet<BlechNode>) (fnct : BlechNode -> bool): BlechNode =
            nodes 
            |> Seq.toList 
            |> List.tryFind fnct 
            |> (fun option -> match option.IsSome with true -> option.Value | false -> failwith("No node with the specified properties found in this graph."))

    /// Finds the node that has Property Init set to true and returns it.
    let findInitNodeInHashSet(nodes : HashSet<BlechNode>) : BlechNode =
            findNodeInHashSet nodes (fun node -> match node.Payload.IsInitOrFinal.Init with IsInit -> true | _ -> false)
    
    /// Finds the node that has Property Init set to true and returns it.
    let findFinalNodeInHashSet(nodes : HashSet<BlechNode>) : BlechNode =
            findNodeInHashSet nodes (fun node -> match node.Payload.IsInitOrFinal.Final with IsFinal -> true | _ -> false)

    /// Determines if there is a final node in a set of nodes.
    let isThereFinalNodeInHashSet(nodes : HashSet<BlechNode>) : bool =
         nodes 
         |> Seq.toList 
         |> List.tryFind (fun node -> match node.Payload.IsInitOrFinal.Final with IsFinal -> true | _ -> false) 
         |> (fun option -> option.IsSome)
    
    /// Checks a cobegin body for the presence of a final node in any of the regions.
    let rec isThereFinalNodeInCobegin (cbgnBody :(VisGraph * Strength) list) : bool = 
        match cbgnBody with
            | head :: tail -> if isThereFinalNodeInHashSet (fst head).Nodes then
                                true
                              else 
                                isThereFinalNodeInCobegin tail
            | [] -> false
    
    /// Determines if there is a final node in a set of nodes and returns its ids.
    let stateCountAndSecondaryOfFinalNodeIfPresent (nodes : HashSet<BlechNode>) : Option<int * int> =
         nodes 
         |> Seq.toList 
         |> List.tryFind (fun node -> match node.Payload.IsInitOrFinal.Final with IsFinal -> true | _ -> false) 
         |> (fun option -> 
                match option.IsSome with true -> Some (option.Value.Payload.StateCount, option.Value.Payload.SecondaryId) | _ -> None)

    /// Determines if apart of this edge, other edges between source and target are present.
    let multSpecifiedAndSingleOtherEdge (propertyMult : EdgeProperty) (propertySingle : EdgeProperty) (edge : BlechEdge) (edges : BlechEdge list) : bool =
        if (not (List.contains edge edges)) then failwith "Expected given edge to be part of given list. Was not the case."

        // Now check if edges have same source and target.
        let counter = 
            fun (tuple: int * BlechEdge list) (e:BlechEdge) -> 
                if matchNodes e.Source edge.Source && matchNodes e.Target edge.Target then 
                    (fst tuple + 1, e :: (snd tuple))
                else 
                    (fst tuple, snd tuple)
        let count = List.fold counter (0, []) edges
        let detectedEdges = snd count
        let foldMatchProp = (fun (prop : EdgeProperty) (acc:int) (e:BlechEdge) -> match e.Payload.Property = prop with true -> acc + 1 | false -> acc)
        let countSingle = List.fold (foldMatchProp propertySingle) 0 detectedEdges
        let countMult = List.fold (foldMatchProp propertyMult) 0 detectedEdges
        
        // We want exactly two edges between source and target. One abort and one immediate, others are unknown and unconsidered cases.
        (fst count) > 1 && countSingle = 1 && countMult > 0

    /// Checks whether the source and target of the edge have only immediate edges between them.
    /// Either focuses on source (focusOnSource = true) or target (false).
    let onlyImmediatesTerminalsOrConditionals (edge : BlechEdge) (focusOnSource): bool = 
        let source = edge.Source
        let target = edge.Target
        let sourceOutgoings = (Seq.toList source.Outgoing)
        let targetIncomings = (Seq.toList target.Incoming)

        let cond1 = sourceOutgoings.Length >= 2 && targetIncomings.Length >= 2
        let edgesEqualToEdge = 
            (fun acc (e:BlechEdge) -> acc && matchNodes e.Source source && matchNodes e.Target target)
        let edgeTerminalOrImmediate =
            (fun acc (e:BlechEdge) -> 
                acc && (e.Payload.Property = IsImmediate || e.Payload.Property = IsTerminal || e.Payload.Property = IsConditional || e.Payload.Property = IsConditionalTerminal))         
        let cond2 = if focusOnSource then List.fold edgesEqualToEdge true sourceOutgoings else List.fold edgesEqualToEdge true targetIncomings 
        let cond3 = if focusOnSource then List.fold edgeTerminalOrImmediate true sourceOutgoings else List.fold edgeTerminalOrImmediate true targetIncomings

        cond1 && cond2 && cond3

    /// Finds for a node that is calling an activity, whether said activity contains no final node.
    /// This is given by the list of pairs, pairing the acitvity names with the presence indicator.
    /// If node is activity call, and called activity has NO final node, return true, else false.
    let rec nodeIsActivityCallAndHasNoFinalNode (current: BlechNode) (pairs : (string*bool) list) : bool =
        match current.Payload.IsComplex with
            | IsActivityCall call -> let pair = List.find (fun e -> call.origName = fst e) pairs
                                     not (snd pair)
            | _ -> false

    /// Checks if a node is a cobegin and if such does not has a final node in a region.
    and nodeIsCbgnAndHasNoFinalNode (current: BlechNode) : bool =
        match current.Payload.IsComplex with
            | IsCobegin cbgn -> not (isThereFinalNodeInCobegin cbgn.Content)
            | _ -> false

    /// Finds for a node that is calling an activity, whether said activity contains a final node.
    /// This is given by the list of pairs, pairing the acitvity names with the presence indicator.
    /// If node is activity call, and called activity has final node, return true, else false.
    and nodeIsActivityCallAndHasFinalNode (current: BlechNode) (pairs : (string*bool) list) : bool =
        not (nodeIsActivityCallAndHasNoFinalNode current pairs)

    /// Checks if a node is a cobegin and if such has a final node in a region.
    and nodeIsCbgnAndHasFinalNode (current: BlechNode) : bool =
        not (nodeIsCbgnAndHasNoFinalNode current)

    /// Checks if a node is complex and has a final node.
    let nodeIsCmplxAndHasFinalNode (finalNodeInfo : (string * bool) list) (node : BlechNode) : bool =
        match node.Payload.IsComplex with
            | IsSimple | IsConnector -> false
            | IsComplex cmplx -> isThereFinalNodeInHashSet cmplx.Body.Nodes
            | IsCobegin _-> nodeIsCbgnAndHasFinalNode node
            | IsActivityCall _ -> nodeIsActivityCallAndHasFinalNode node finalNodeInfo

    //____________________________________Remove element in list.
    /// Removes element from a list. If element is not in list, original list will be returned.
    let rec removeItem (item : 'T) (list : 'T list) =
        match item, list with
            | item, head :: tail -> if(item = head) then removeItem item tail else head :: removeItem item tail
            | _, [] -> []

    //_________________________________Add graph to graph in single not failable steps.
    /// Adds a given graph to a graph by imitating the nodes and replicating the edges (creating brand new objects that is).
    let rec addGraphToGraph (graph : VisGraph) (graphToAdd : VisGraph) : VisGraph = 
        // 1. Add all nodes from the graph with their respectve payloads.
        graphToAdd.Nodes |> Seq.iter (fun n -> graph.AddNode n.Payload |> ignore)        
        // 2. Imitate edges as given, for this find the corresponding now existing nodes from step 1 and add a new edge with these nodes and the given edge data.
        graphToAdd.Edges |> 
         Seq.iter (fun e -> graph.AddEdge e.Payload 
                                          (findNodeByStateCount e.Source.Payload.StateCount e.Source.Payload.SecondaryId graph) 
                                          (findNodeByStateCount e.Target.Payload.StateCount e.Target.Payload.SecondaryId graph))
        graph

    /// Finds a specific Blechnode in a given list of nodes, specified by the StateCount of the desired node and the secondary identifier.
    and findNodeByStateCount (desiredCount: int) (desiredSecondary : int) (graph: VisGraph) : BlechNode =
        graph.Nodes |> Seq.find (fun n -> n.Payload.StateCount = desiredCount && n.Payload.SecondaryId = desiredSecondary)