module Blech.Visualization.Simplification
    
    open Blech.Common
    open Blech.Common.GenericGraph
    open Blech.Frontend.CommonTypes
    open Blech.Visualization.BlechVisGraph

    //______________________________Global variables_______________________________________________________
    /// Keeps track of which edges have been simplified yet.
    /// First pair are the ids of the source. Second pair are the ids of the target.
    /// ((Source.StateCount, Source.SecondaryId), (Target.StateCount, Target.SecondaryId))
    let mutable private simplifiedEdges: ((int * int) * (int * int)) list = []

    /// Keeps track of which nodes have been hierarchy simplified.
    /// (StateCount , SecondaryId).
    let mutable private simplifiedNodes: (int * int) list = []

    /// Keeps track of which nodes have been secondary id assigned.
    /// (StateCount , SecondaryId).
    let mutable private secondaryIdAssignedList : (int * int) list = []

    /// Keeping track of secondary id.
    let mutable private secondaryId = 0

    /// Keeping track whether activities need to be inlined. 
    let mutable inlineActs = false

    /// Keeping track if an alternative cobegin pattern should be visualized.
    let mutable cobeginPattern = false

    /// Keeping track if the cobegin pattern should be detected at all.
    let mutable cbgnPatternWithHierarchy = false

    /// Keeping track if a connector state is to be used if possible.
    let mutable useConnectorState = false

    /// Keeping track if hierarchy is to broken.
    let mutable noBreakHier = false

    //// Keeping track if transient transitions are to be collapsed.
    let mutable noCollTransient = false

    //______________________________CENTRAL FUNCTION_______________________________________________________
    // Checks an activity node, whether it has a final state. Returns the name of the activity and a boolean inidacting the presence of a final state.
    let private checkForNameAndFinalNode(activityNode : BlechNode) : string * bool = 
        let actNodePayload = activityNode.Payload
        // Extract body.
        let isComplex = match actNodePayload.IsComplex with 
                            | IsComplex a -> a
                            | _ -> failwith "unexpected error, was not an activity node."// Should not happen here.

        (actNodePayload.Label, isThereFinalNodeInHashSet isComplex.Body.Nodes)
    
    /// Simplifies the nodes and their contents according to the simplification steps introduced in the thesis.
    /// Simplifications steps: flatten hierarchy, collapsing transient states.
    /// 4 Bool flags, use conenction states on hierarchy collapse, no cobegin pattern, alternative cobegin pattern, inline activities.
    let rec simplify (cliContext: Arguments.BlechCOptions)
                     (inlineActivities: bool)
                     (entryPointName : string)
                     (activityNodes: BlechNode list) : BlechNode list =
        inlineActs <- inlineActivities
        cobeginPattern <- cliContext.vis_cbgnPattern
        cbgnPatternWithHierarchy <- cliContext.vis_cbgnPatternWithHier
        useConnectorState <- not (cliContext.vis_notUseConnector)
        noBreakHier <- cliContext.vis_disableBreakHier
        noCollTransient <- cliContext.vis_disableCollapseTrans
        let actNameAndFinalNodesPairs = List.map checkForNameAndFinalNode activityNodes
        match inlineActivities with
            | true ->  [simplifySingleActivity activityNodes actNameAndFinalNodesPairs (List.find (fun (n:BlechNode) -> n.Payload.Label = entryPointName) activityNodes)]
            | false -> let firstIt = List.map (simplifySingleActivity activityNodes actNameAndFinalNodesPairs) activityNodes
                       // Doing it twice. Activities are simplified sequentially. Some information is different, after a activity was updated.
                       // TODO make this functionally? So that the part where the updated information is needed is done only?
                       let actNameAndFinalNodesPairs = List.map checkForNameAndFinalNode firstIt
                       let secondIt = List.map (simplifySingleActivity firstIt actNameAndFinalNodesPairs) firstIt
                       // TODO some (updated edges) are not checked again. Hence, the transient simplification is run twice. Fix !
                       let actNameAndFinalNodesPairs = List.map checkForNameAndFinalNode secondIt
                       List.map (simplifySingleActivity secondIt actNameAndFinalNodesPairs) secondIt

    /// Simplifies a single activity node.
    and private simplifySingleActivity (activityNodes: BlechNode list) (finalNodeInfo: (string * bool) list) (activityNode: BlechNode) : BlechNode =
        let actNodePayload = activityNode.Payload
        // Extract body.
        let isComplex = match actNodePayload.IsComplex with 
                        | IsComplex a -> a
                        | _ -> failwith "unexpected error, was not an activity node."// Should not happen here.
        let body = isComplex.Body
        let actPayload = isComplex.IsActivity   

        //Flatten hierarchy inside activity.
        simplifiedNodes <- []
        let flattenedBody = 
            match noBreakHier with
                | true -> body
                | false -> flattenHierarchyIfComplex false activityNodes finalNodeInfo (findInitNodeInHashSet body.Nodes) body

        // Collapse transient states.
        simplifiedEdges <- []
        let collapsedTransientStatesBody = 
            match noCollTransient with
                | true -> flattenedBody
                | false -> collapseTransient finalNodeInfo flattenedBody

        // Put changed body in new node and return it.
        let newComplex : ComplexOrSimpleOrCobegin = IsComplex {Body = collapsedTransientStatesBody; IsActivity = actPayload; CaseClosingNode = {Opt = None}; IsAbort = Neither}
        BlechNode.Create{Label = actNodePayload.Label; 
                         IsComplex = newComplex; 
                         IsInitOrFinal = actNodePayload.IsInitOrFinal; 
                         StateCount = actNodePayload.StateCount;
                         SecondaryId = actNodePayload.SecondaryId; 
                         WasVisualized = NotVisualized}

    //______________________________FLATTEN HIERARCHY (NOT COBEGIN OR ACTIITY CALLS)_______________________________________________________
    /// Flattens a given graph if node is complex, else just call flattening method on successors.
    /// assignSecondaryId determines if the current secondary id is applied through all complexity layers.
    and private flattenHierarchyIfComplex (assignSecondaryId : bool) (activityNodes: BlechNode list) (finalNodeInfo: (string * bool) list) (currentNode : BlechNode) (graph : VisGraph) : VisGraph = 
        // Do not call method on same item again when there are self-loops.
        let filterForUnsimplified = fun (n : BlechNode) -> not (List.contains (n.Payload.StateCount, n.Payload.SecondaryId) simplifiedNodes)
        let successorsWithoutCurrent = (removeItem currentNode (Seq.toList currentNode.Successors))
        let unoptedSuccesssors = List.filter filterForUnsimplified successorsWithoutCurrent

        // Is current node complex? 
        let currentGraph = match currentNode.Payload.IsComplex with
                            | IsSimple | IsConnector -> simplifiedNodes <- (currentNode.Payload.StateCount, currentNode.Payload.SecondaryId) :: simplifiedNodes
                                                        graph
                            | IsActivityCall _ -> match inlineActs with
                                                    | true -> flattenHierarchyActivityCall activityNodes finalNodeInfo currentNode graph
                                                    | false -> simplifiedNodes <- (currentNode.Payload.StateCount, currentNode.Payload.SecondaryId) :: simplifiedNodes
                                                               graph
                            | IsCobegin cbgn -> flattenHierarchyCobegin activityNodes finalNodeInfo currentNode cbgn graph
                            | IsComplex cmplx -> // Do not flatten if weak abort.
                                                 match cmplx.IsAbort with
                                                    | WeakAbort -> simplifiedNodes <- (currentNode.Payload.StateCount, currentNode.Payload.SecondaryId) :: simplifiedNodes
                                                                   graph
                                                    | _ -> flattenHierarchy assignSecondaryId activityNodes finalNodeInfo currentNode cmplx graph

        // It is possible to wrongly assign the final statsus to a node with information based on not yet simplified acitivites (that are called through run statements).
        //Check if said status is rightful. Check if inner behaviour has been checked.
        let noFinalAct = nodeIsActivityCallAndHasNoFinalNode currentNode finalNodeInfo
        let noFinalCbgn = nodeIsCbgnAndHasNoFinalNode currentNode
        if noFinalAct || noFinalCbgn then graph.ReplacePayloadInBy currentNode (currentNode.Payload.SetFinalStatusOff)

        callFlatHierarchyOnNodes assignSecondaryId activityNodes finalNodeInfo unoptedSuccesssors currentGraph

    /// Flattens the hierarchy on a list of nodes subsequentially.
    and private callFlatHierarchyOnNodes (assignSecondaryId : bool) (activityNodes: BlechNode list) (finalNodeInfo: (string * bool) list) (nodes : BlechNode list) (graph : VisGraph) : VisGraph = 
        List.fold (fun state e-> flattenHierarchyIfComplex assignSecondaryId activityNodes finalNodeInfo e state) graph nodes

    /// Replaces the payloads of the nodes in the given graph with the payloads resulting from the given function.
    and private replacePayloadsInGraph (convertToPayload : BlechNode -> NodePayload) (graph : VisGraph) : VisGraph =
        List.map (fun node -> graph.ReplacePayloadInByAndReturn node (convertToPayload node)) (Seq.toList graph.Nodes) |> ignore
        graph

    /// Gives a new payload of the given node's payload with the prefix added to the node's label, if given bool is true.
    and addPrefixToNodeLabel (prefix : string) (node : BlechNode) = 
        match node.Payload.Label with
           | "" -> node.Payload.SetLabel prefix
           | _ -> node.Payload.SetLabel (prefix + "-" + node.Payload.Label)

    /// Gives a new payload of the given node's payload with the postfix added to the node's label.
    and addPostdixToNodeLabel (postfix : string) (node : BlechNode) = 
        match node.Payload.Label with
           | "" -> node.Payload.SetLabel postfix
           | _ -> node.Payload.SetLabel (node.Payload.Label + "-" + postfix)

    /// Sets the secondary id as as secondary id of the nodes in the given graph, if they have not been assigned a secondary id before.
    /// Also minds the saved ids of the case closing nodes.
    and private setSecondaryIdOnSubGraph (graph: VisGraph) : VisGraph = 
        let notAssignedYet = fun (n : BlechNode) -> not (List.contains (n.Payload.StateCount, n.Payload.SecondaryId) secondaryIdAssignedList)
        let setSecondaryIdIfOk = 
            fun (n:BlechNode) -> 
                // Assign new values recursively.
                if notAssignedYet n then
                    secondaryIdAssignedList <- (n.Payload.StateCount, secondaryId) :: secondaryIdAssignedList
                    // Also sets the case closing node, if present.
                    let updatedPl = n.Payload.SetSecondaryId secondaryId

                    // Recursive call.
                    match updatedPl.IsComplex with
                        // Cobegin was done in method Clone Rec. 
                        // IsActivityCall should only appear if inlining is not wanted, in this case, no recursive call needed as the nodes inside an activity are distinct.
                        // Complex payload needs to be cloned !!! Else this payload will point to wrong body.
                        | IsSimple | IsConnector | IsActivityCall _ | IsCobegin _-> updatedPl
                        | IsComplex cmplx -> IsComplex (cmplx.SetSecIdCaseClosingAndBody secondaryId (setSecondaryIdOnSubGraph (cloneRec false cmplx.Body)))
                                              |> updatedPl.SetComplex
                else
                    n.Payload

        replacePayloadsInGraph setSecondaryIdIfOk graph |> ignore
        graph

    /// Clones a graph recursively. That means that special payloads, such as cobegins, are cloned explicitly. 
    /// Else it can cause, that two cloned cobegin nodes point to the same subgraph. Changing this in said subgraph will cause trouble.
    /// AssignSecondaryId tells us, that this is a body of an activity that is called.
    and private cloneRec (assignSecondaryId : bool) (graph: VisGraph) : VisGraph =
        let simplyCloned = clone graph

        if assignSecondaryId then secondaryId <- secondaryId + 1

        // Clones cobegin payload.
        let cloneCobeginPayload = fun (cbgn : CobeginPayload) -> let updatedList = List.map (fun (e: VisGraph * Strength)-> let cloned = clone (fst e)
                                                                                                                            let clonedUpdated = 
                                                                                                                                if assignSecondaryId then
                                                                                                                                    setSecondaryIdOnSubGraph cloned
                                                                                                                                else
                                                                                                                                    cloned
                                                                                                                            (clonedUpdated, snd e))
                                                                                            cbgn.Content
                                                                 IsCobegin {Content = updatedList; CaseClosingNode = cbgn.CaseClosingNode}

        // Check a node and replace its payload if necessary.
        let checkAndReplace = fun (n:BlechNode) -> 
                                match n.Payload.IsComplex with
                                    | IsCobegin cbgn -> simplyCloned.ReplacePayloadInBy n (n.Payload.SetComplex ((cloneCobeginPayload cbgn).SetSecondaryIdOfCaseClosingNode secondaryId)) 
                                    | _ -> ()
        List.map checkAndReplace (Seq.toList simplyCloned.Nodes) |> ignore
        
        if assignSecondaryId then 
            setSecondaryIdOnSubGraph simplyCloned
        else 
            simplyCloned

    /// Elevates the inner body of a complex node to the level given in graph. Collapses hierarchies recursively regarding all hierarchies that are not caused by activites.
        // 1. Change the status of the inner init/final state, so that they are regular states.
        // 2. Join inner graph with current graph. 
        // 4. Modify in- and outcoming edges from node and change their source/target to the final/init node of the inner graph, respecitvely.
        // 5. Respect the differences in handling edges (aborts, for example). Some completely new edges might have to be added.
        // 6. Remove node from graph.
    and private flattenHierarchy (assignSecondaryId : bool) (activityNodes: BlechNode list) (finalNodeInfo: (string * bool) list) (currentNode : BlechNode) (complex : ComplexNode) (graph : VisGraph) : VisGraph = 
        let complexCloned = cloneRec assignSecondaryId complex.Body

        // Recursive hierarchy flattening call on inner graph.
        // Give correct secondary id to mark as simplified, as secondary id will be increased AFTER these opt steps.
        let innerGraph = flattenHierarchyIfComplex assignSecondaryId activityNodes finalNodeInfo (findInitNodeInHashSet complexCloned.Nodes) complexCloned

        // Init.
        let init = findInitNodeInHashSet innerGraph.Nodes
        // Replace init only if current is not init! 
        let replacedInit = 
            match currentNode.Payload.IsInitOrFinal.IsInitBool with 
                | true -> init
                | false -> innerGraph.ReplacePayloadInByAndReturn init (init.Payload.SetInitStatusOff)
        let innerInitStateCount = replacedInit.Payload.StateCount
        let innerInitSecondaryId = replacedInit.Payload.SecondaryId
        let innerNodesIds = List.map findIds (Seq.toList innerGraph.Nodes)
        let finalNodePresent = isThereFinalNodeInHashSet innerGraph.Nodes

        let initGraphPair = 
            match finalNodePresent with 
                | true ->
                    let final = findFinalNodeInHashSet innerGraph.Nodes
                    // Replace final only if current is not final! 
                    let replacedFinal = 
                        match currentNode.Payload.IsInitOrFinal.IsFinalBool with 
                            | true -> final
                            | false -> innerGraph.ReplacePayloadInByAndReturn final (final.Payload.SetFinalStatusOff)
                    let innerFinalStateCount = replacedFinal.Payload.StateCount
                    let innerFinalSecondary = replacedFinal.Payload.SecondaryId
                    let joinedGraph = addGraphToGraph graph innerGraph

                    let newInit = findNodeByStateCount innerInitStateCount innerInitSecondaryId joinedGraph
                    let newFinal = findNodeByStateCount innerFinalStateCount innerFinalSecondary joinedGraph

                    // Update edges.
                    (updateEdgesFlattenHierarchy (Seq.toList currentNode.Incoming) newInit Target joinedGraph 
                        |> updateEdgesFlattenHierarchy (Seq.toList currentNode.Outgoing) newFinal Source
                   , newInit)
                | false -> 
                    let joinedGraph = addGraphToGraph graph innerGraph
                    let newInit = findNodeByStateCount innerInitStateCount innerInitSecondaryId joinedGraph
            
                    // Update edges.
                    (updateEdgesFlattenHierarchy (Seq.toList currentNode.Incoming) newInit Target joinedGraph, newInit)
        let updatedGraph = fst initGraphPair
        let newInit = snd initGraphPair

        // Add abort transitions according to the concept from the inner graph to either the former initial state of the inner graph or the case closing state, depending on the abort.
        // TODO there has got to be some possible optimizations here.
        match complex.IsAbort with
            | AbortWhen label -> let caseClosingNode = findNodeByStateCount (complex.CaseClosingNode.StateCount) (complex.CaseClosingNode.SecondaryId) updatedGraph
                                 let firstAwaitAndSubsequentConstruct = findFirstAwaitNodeOnEveryPath newInit innerNodesIds [findIds newInit]
                                 let subsequentNodes = frst3 firstAwaitAndSubsequentConstruct
                                 let addEdgeToEveryFirstAwait = 
                                    fun (o:Option<BlechNode>) -> 
                                        match o with 
                                            | Some a -> addEdgeToNode caseClosingNode IsAbort label updatedGraph a
                                            | None -> () // Do nothing.   
                                 List.map addEdgeToEveryFirstAwait (scnd3 firstAwaitAndSubsequentConstruct) |> ignore
                                 List.map (addEdgeToNode caseClosingNode IsAbort label updatedGraph) subsequentNodes |> ignore
            | AbortRepeat label -> let firstAwaitAndSubsequentConstruct = findFirstAwaitNodeOnEveryPath newInit innerNodesIds [findIds newInit]
                                   let subsequentNodes = frst3 firstAwaitAndSubsequentConstruct
                                   let addEdgeToEveryFirstAwait = 
                                        fun (o:Option<BlechNode>) -> 
                                            match o with 
                                                | Some a -> addEdgeToNode newInit IsAbort label updatedGraph a  
                                                | None -> () // Do nothing.   
                                   List.map addEdgeToEveryFirstAwait (scnd3 firstAwaitAndSubsequentConstruct) |> ignore
                                   List.map (addEdgeToNode newInit IsAbort label updatedGraph) subsequentNodes |> ignore
            | WeakAbort | Neither -> () // Do nothing.

        let nodeToRemove = List.find (matchNodes currentNode) (Seq.toList updatedGraph.Nodes)
        updatedGraph.RemoveNode nodeToRemove
        updatedGraph

    /// Adds a list of new edges to the graph.
    /// New edges are based on the data given by the edges, the information whether source or target is to be changed and the given node to be the new source/target.
    /// Join Transitions are changed to immediate transitions.
    and private updateEdgesFlattenHierarchy (edgeList : BlechEdge list) (newTargetOrSource : BlechNode) (sourceOrTarget : SourceOrTarget) (graph : VisGraph): VisGraph = 
        match edgeList with 
            | head :: tail  ->  let updatedTargetOrSource = 
                                    match sourceOrTarget with
                                        | Source -> // Determine payload. Terminal transitions change to immdediate transitions because the hierarchy is flattened.
                                                    let payload = 
                                                        match head.Payload.Property with
                                                            | IsAwait | IsConditional | IsImmediate | IsAbort -> head.Payload
                                                            | IsTerminal -> head.Payload.CopyWithProperty IsImmediate
                                                            | IsConditionalTerminal -> head.Payload.CopyWithProperty IsConditional
                                                            | IsTerminalAwait -> head.Payload.CopyWithProperty IsAwait
                                        
                                                    // 1. If the final node of an complex body becomes a node that is the source of conditionals, it is a connector really.
                                                    // Except if it is the source of a delayed transition.
                                                    let updatedSource = 
                                                        if newTargetOrSource.Payload.IsComplex = IsSimple && payload.Property = IsConditional && useConnectorState &&
                                                            not (checkEdgesForAwait (Seq.toList newTargetOrSource.Outgoing)) then
                                                            graph.ReplacePayloadInByAndReturn newTargetOrSource (newTargetOrSource.Payload.SetComplex IsConnector)
                                                        else
                                                            newTargetOrSource
                                                    // Due to being a new "node", transitions in the edge list might point to inacurrate nodes (self-loops especially).
                                                    // Get real nodes from graph.
                                                    let target = findNodeByStateCount head.Target.Payload.StateCount head.Target.Payload.SecondaryId graph
                                                    graph.AddEdge payload updatedSource target
                                                    updatedSource
                                        | Target -> graph.AddEdge head.Payload head.Source newTargetOrSource
                                                    newTargetOrSource
                                updateEdgesFlattenHierarchy tail updatedTargetOrSource sourceOrTarget graph
            | [] -> graph

    //______________________________FLATTEN HIERARCHY (ACT CALL)_______________________________________________________
    /// Elevates the inner graph of another activity to a higher level. Activity is given by activityNodes. Current node is an activity call that is to be deleted.
    and private flattenHierarchyActivityCall (activityNodes: BlechNode list) (finalNodeInfo: (string * bool) list) (currentNode : BlechNode) (graph : VisGraph) : VisGraph = 
        // Find correct activity from list.
        let activityNode = List.find (fun (e:BlechNode) -> e.Payload.Label = currentNode.Payload.GetActivityOrigLabel) activityNodes
        let cmplx = match activityNode.Payload.IsComplex with 
                        | IsComplex a -> a
                        | _ -> failwith "unexpected error, was not an activity node."// Should not happen here.

        flattenHierarchy true activityNodes finalNodeInfo currentNode cmplx graph

    //______________________________FLATTEN HIERARCHY (COBEGIN)_______________________________________________________
    /// Adds an  edge to the given graph with the given label, source and target and given property.
    and private addEdgeToNode (target : BlechNode) (property : EdgeProperty) (label: string) (graph : VisGraph) (source : BlechNode) =     
        graph.AddEdge {Label = label; Property = property; WasSimplified = NotSimplified} source target
     
    /// Adds an immediate or termintation edge to the given graph with the given label, source and target. Distinction depends on complexity of the source.
    and private addImmedOrTerminEdgeToNode (target : BlechNode) (label: string) (graph : VisGraph) (source : BlechNode) =     
        match source.Payload.IsComplex with
            | IsConnector -> ()
            | IsSimple -> graph.AddEdge {Label = label; Property = IsImmediate; WasSimplified = NotSimplified} source target
            | _ -> graph.AddEdge {Label = label; Property = IsTerminal; WasSimplified = NotSimplified} source target
    
    /// Checks, whether a graph contains only a single await statement.
    // TODO seriously with this method? Rework this for the love of 42. It works, but come on.
    and private onlyAwaitStmt (graph : VisGraph) : bool = 
        // This step is executed pre immediate-transition simplification.
        // Hence a single await statement should look like this:
        // initial -await- regular_node -immediate- final
        let init = findInitNodeInHashSet (graph.Nodes)
        match (Seq.toList init.Outgoing).Length = 1 with
            | true ->   let possibleAwaitEdge = (Seq.toList init.Outgoing).[0]
                        match possibleAwaitEdge.Payload.Property = IsAwait with
                            | true ->   let awaitTarget = possibleAwaitEdge.Target
                                        match (Seq.toList awaitTarget.Outgoing).Length = 1 with
                                            | true -> (Seq.toList awaitTarget.Outgoing).[0].Target.Payload.IsInitOrFinal.IsFinalBool
                                            | false -> false
                            | false -> false
            | false -> false

    // Checks for a pair of regions, if there is one weak region, while the other non-confirmed weak region contains only exactly one await statement.
    and private checkRegionWeakAndContainAwait (firstR : VisGraph * Strength) (secondR : VisGraph * Strength) : bool =
        (snd firstR = Weak && onlyAwaitStmt (fst secondR)) || (snd secondR = Weak && onlyAwaitStmt (fst firstR))    
    
    /// In a pair of regions, order them so that the weak region that does not contain only an await statement to come first. The latter is the condition of the await and its strength.
    /// This method has the constraint that the two regions return true when put in the method checkRegionWeakAndContainAwait.
    and private orderRegions (firstR : VisGraph * Strength) (secondR : VisGraph * Strength) : (VisGraph * Strength) * (string * Strength) =
        if not (checkRegionWeakAndContainAwait firstR secondR) then failwith "The given regions are not fit to be used in this method."
        let getAwaitCond = fun (graph : VisGraph) -> (Seq.toList (findInitNodeInHashSet graph.Nodes).Outgoing).[0].Payload.Label                                         

        if (snd firstR = Weak && onlyAwaitStmt (fst secondR)) then
            (firstR, (getAwaitCond (fst secondR), snd secondR))
        else 
            (secondR, (getAwaitCond (fst firstR), snd firstR))

    /// Elevates the inner body of a cobegin node to the level given in graph, iff certain patterns are matched. 
    /// If a certain flag was set in the beginning of the program. Add a hierarchical node that contains the code mof the non-await branch and at await condition as a weak abort.
    /// Collapses hierarchies recursively regarding all hierarchies that are not caused by activites for every branch.
    and private flattenHierarchyCobegin (activityNodes: BlechNode list) (finalNodeInfo: (string * bool) list) (currentNode : BlechNode) (complex : CobeginPayload) (graph : VisGraph) : VisGraph =
        // Call flattening recursively on branches.
        List.map (fun (b : VisGraph * Strength) -> (flattenHierarchyIfComplex false activityNodes finalNodeInfo (findInitNodeInHashSet (fst b).Nodes) (fst b))) complex.Content |> ignore
        
        // 1. Two regions, at least one weak. Other must contain a single await statement ONLY
        let generalCondition = complex.Content.Length = 2 && checkRegionWeakAndContainAwait complex.Content.[0] complex.Content.[1] && cobeginPattern
        let cond1 = generalCondition && (not cbgnPatternWithHierarchy)
        let cond2 = generalCondition && cbgnPatternWithHierarchy
        if cond1 then
            let orderedPairOfRegions = orderRegions complex.Content.[0] complex.Content.[1]
            let graphToBeElevated = clone (fst (fst orderedPairOfRegions))

            // Init.
            let init = findInitNodeInHashSet graphToBeElevated.Nodes
            let replacedInit = graphToBeElevated.ReplacePayloadInByAndReturn init (init.Payload.SetInitStatusOff)
            let innerInitStateCount = replacedInit.Payload.StateCount
            let innerInitSecondaryId = replacedInit.Payload.SecondaryId
            let innerNodesIds = List.map findIds (Seq.toList graphToBeElevated.Nodes)
            let finalNodePresent = isThereFinalNodeInHashSet graphToBeElevated.Nodes
            let innerFinalStateCountAndSecondaryIfPresent = stateCountAndSecondaryOfFinalNodeIfPresent graphToBeElevated.Nodes

            let initGraphPair = 
                match finalNodePresent with 
                    | true ->
                        let final = findFinalNodeInHashSet graphToBeElevated.Nodes
                        graphToBeElevated.ReplacePayloadInByAndReturn final (final.Payload.SetFinalStatusOff) |> ignore

                        let joinedGraph = addGraphToGraph graph graphToBeElevated
                        let newInit = findNodeByStateCount innerInitStateCount innerInitSecondaryId joinedGraph

                        // Update edges. Not outgoing ones, they are special for the cobegin pattern and added explicitly. Edges such as aborts can not occur.
                        (updateEdgesFlattenHierarchy (Seq.toList currentNode.Incoming) newInit Target joinedGraph,
                         newInit)
                    | false -> 
                        let joinedGraph = addGraphToGraph graph graphToBeElevated
                        let newInit = findNodeByStateCount innerInitStateCount innerInitSecondaryId joinedGraph
                
                        // Update edges.
                        (updateEdgesFlattenHierarchy (Seq.toList currentNode.Incoming) newInit Target joinedGraph, newInit)
            let updatedGraph = fst initGraphPair
            let newInit = snd initGraphPair

            // Add transitions according to the concept from the inner graph to the case closing state.
            let caseClosingNode = findNodeByStateCount (complex.CaseClosingNode.StateCount) (complex.CaseClosingNode.SecondaryId) updatedGraph
            let findFirstAwaitConstruct = (findFirstAwaitNodeOnEveryPath newInit innerNodesIds [findIds newInit])
            let allAwaitAndSubsequentNodesInSupgraph = frst3 findFirstAwaitConstruct
            
            // First await.
            let addEdgeToEveryFirstAwait = 
                                        fun (o:Option<BlechNode>) -> 
                                            match o with 
                                                | Some a -> addEdgeToNode caseClosingNode IsAwait (fst (snd orderedPairOfRegions)) updatedGraph a  
                                                | None -> () // Do nothing.   
            List.map addEdgeToEveryFirstAwait (scnd3 findFirstAwaitConstruct) |> ignore               
            //All nodes after first await. Add edge manually for very last node, if present.
            List.map (addEdgeToNode caseClosingNode IsImmediate (fst (snd orderedPairOfRegions)) updatedGraph) allAwaitAndSubsequentNodesInSupgraph |> ignore
            if (finalNodePresent) then 
                let innerStateIds = innerFinalStateCountAndSecondaryIfPresent.Value
                let innerFinalNode = findNodeByStateCount (fst innerStateIds) (snd innerStateIds) updatedGraph
                addEdgeToNode caseClosingNode IsImmediate "" updatedGraph innerFinalNode

                // Add edge from last to case closing, depending on strength of await-region. Edge is a conditionsless edge. ( Is termination edge if source is complex.)
                if (snd (snd orderedPairOfRegions)) = Weak then
                    addImmedOrTerminEdgeToNode caseClosingNode "" updatedGraph innerFinalNode

            let nodeToRemove = List.find (matchNodes currentNode) (Seq.toList updatedGraph.Nodes)
            updatedGraph.RemoveNode nodeToRemove
            updatedGraph
        elif cond2 then 
            // Extract info from regions.
            let orderedPairOfRegions = orderRegions complex.Content.[0] complex.Content.[1]
            let graphToBeElevated = clone (fst (fst orderedPairOfRegions))
            let caseClosingNode = findNodeByStateCount (complex.CaseClosingNode.StateCount) (complex.CaseClosingNode.SecondaryId) graph

            // Create new complex node information and replace old payload in node.
            let newCmplx = IsComplex {Body = graphToBeElevated; 
                                      IsActivity = IsNotActivity; 
                                      CaseClosingNode = {Opt = Some (findIds caseClosingNode)}; 
                                      IsAbort = WeakAbort};
            let newPld = {Label = currentNode.Payload.Label; 
                          IsComplex = newCmplx; 
                          IsInitOrFinal = currentNode.Payload.IsInitOrFinal; 
                          StateCount = currentNode.Payload.StateCount;
                          SecondaryId = currentNode.Payload.SecondaryId; 
                          WasVisualized = NotVisualized}
            let updatedCurr = graph.ReplacePayloadInByAndReturn currentNode newPld

            // Add weak abort transition.
            addEdgeToNode caseClosingNode IsImmediate (fst (snd orderedPairOfRegions)) graph updatedCurr
            graph
        else
           simplifiedNodes <- (currentNode.Payload.StateCount, currentNode.Payload.SecondaryId) :: simplifiedNodes
           graph

    //______________________________COLLAPSE IMMEDIATE TRANSITIONS_______________________________________________________ 
    /// Starting point for collapsing transient transitions.
    and private collapseTransient (finalNodeInfo : (string * bool) list) (graph : VisGraph) : VisGraph =
        let initNodes = findInitNodeInHashSet graph.Nodes
        checkEdgesForCollapse finalNodeInfo (Seq.toList initNodes.Outgoing) graph

    /// Checks if a node is simple or an connector.
    and private isSimpleOrConnector (node : BlechNode) =
        node.Payload.IsComplex = IsSimple || node.Payload.IsComplex = IsConnector 

    ///Method to iterate over an edge of list to check single edges.
    and private checkEdgesForCollapse (finalNodeInfo : (string * bool) list) (edges : BlechEdge list) (graph : VisGraph) : VisGraph = 
        match edges with
            | head :: tail -> checkSingleEdgeForCollapse finalNodeInfo graph head |> checkEdgesForCollapse finalNodeInfo tail
            | [] -> graph

    /// Calls the recursive method on subsequent edges. Avoid edges that are self-loops.
    and private callSubsequentAndFilterAlreadyVisitedTargets (finalNodeInfo : (string * bool) list) (edges : BlechEdge List) (graph : VisGraph) : VisGraph =
        let filterForUnsimplifiedEdges = fun e -> not (List.contains (convertToIdTuple e) simplifiedEdges)
        checkEdgesForCollapse finalNodeInfo (List.filter filterForUnsimplifiedEdges edges) graph

    /// Updates the status of the current node in context of immediate transition deletion. 
    /// Depending on the final and init status of the other node (sourceOrTarget), which is to be deleted, the status of the given node changes.
    and updateStatusOfNodeDependingOfSuccessorOrPredecessor (finalNodeInfo : (string * bool) list) (current : BlechNode) (counterpart : BlechNode) (graph : VisGraph) : BlechNode = 
        let initChecked = match counterpart.Payload.IsInitOrFinal.Init with
                                        | IsInit -> match current.Payload.IsComplex with
                                                        | IsConnector -> graph.ReplacePayloadInByAndReturn current ((current.Payload.SetInitStatusOn).SetComplex IsSimple)
                                                        | _ -> graph.ReplacePayloadInByAndReturn current (current.Payload.SetInitStatusOn)
                                        | _ -> current

        let bothChecked = match counterpart.Payload.IsInitOrFinal.Final with
                            | IsFinal -> // If current is an activity call that does not have a final node, do not reassign final status.
                                         // If current is a cobegin without a final node, do not reassign final status.
                                         let notReassignFinalAct = nodeIsActivityCallAndHasNoFinalNode current finalNodeInfo
                                         let notReassignFinalCbgn = nodeIsCbgnAndHasNoFinalNode current
                                         if notReassignFinalAct || notReassignFinalCbgn then
                                            initChecked
                                         else
                                            graph.ReplacePayloadInByAndReturn initChecked (initChecked.Payload.SetFinalStatusOn)
                            | _ -> initChecked
        bothChecked

    /// Checks a single edge for collaps according to the specifications in the thesis. Checks outgoing transitions as ingoin transitions have been tested by a former step.
    /// Also calls the collapse of immediate trnaasitions recursively to complex nodes.
    // TODO this is not functional programming..
    and private checkSingleEdgeForCollapse (finalNodeInfo : (string * bool) list) (graph : VisGraph) (edge : BlechEdge) : VisGraph =
        //Recursive calls.
        // Doing simplfication on target AND source. This means nodes will be done more often than necessary. But this is needed for the very and very last node. TODO there is probably a better way for this.
        match edge.Source.Payload.IsComplex with 
            | IsComplex cmplx -> collapseTransient finalNodeInfo cmplx.Body
            | IsCobegin cbgn -> immediateCollapseCallOnCobegin finalNodeInfo cbgn.Content graph
            | IsSimple | IsConnector | IsActivityCall _-> graph
        |> ignore

        match edge.Target.Payload.IsComplex with 
            | IsComplex cmplx -> collapseTransient finalNodeInfo cmplx.Body
            | IsCobegin cbgn -> immediateCollapseCallOnCobegin finalNodeInfo cbgn.Content graph
            | IsSimple | IsConnector | IsActivityCall _-> graph
        |> ignore

        let source = edge.Source
        let sourceOutgoings = (Seq.toList source.Outgoing)
        let target = edge.Target
        let targetIncomings = (Seq.toList target.Incoming)

        // Mark the current edge as simplified.
        simplifiedEdges <- convertToIdTuple edge :: simplifiedEdges
        // Special cases. 
        // Only immediate transitions between the source and edge. Source can not be a weak abort.
        let isSourceWeakAbort = match source.Payload.IsComplex with
                                    | IsComplex cmplx -> cmplx.isWeakAbort
                                    | _ -> false
        let onlyImmediatesTerminalOrConditionalSourceFocus = onlyImmediatesTerminalsOrConditionals edge true
        let onlyImmediatesTerminalOrConditionalTargetFocus = onlyImmediatesTerminalsOrConditionals edge false
        let specialCase1 = sourceOutgoings.Length >= 2 && targetIncomings.Length >= 2 &&
                            (onlyImmediatesTerminalOrConditionalSourceFocus || onlyImmediatesTerminalOrConditionalTargetFocus) &&
                            (not isSourceWeakAbort)

        // Special case, abort/await and termination transition. Termination transition origins in an activity call a complex node or a cobegin without final node.
        // Only delete edge, if the current edge is the terminal edge. 
        // Caution: If source has a conditional edge to a different edge.
        let specialCase2 =  (nodeIsActivityCallAndHasNoFinalNode source finalNodeInfo ||
                             (match source.Payload.IsComplex with
                                    | IsComplex cmplx ->not (isThereFinalNodeInHashSet cmplx.Body.Nodes)                    
                                    | IsCobegin cbgn -> not (isThereFinalNodeInCobegin cbgn.Content)
                                    | _ -> false)) &&
                            sourceOutgoings.Length >= 2 && targetIncomings.Length >= 2 &&
                            ((multSpecifiedAndSingleOtherEdge IsTerminal IsAbort edge sourceOutgoings && multSpecifiedAndSingleOtherEdge IsTerminal IsAbort edge targetIncomings) ||
                             (multSpecifiedAndSingleOtherEdge IsTerminal IsAwait edge sourceOutgoings && multSpecifiedAndSingleOtherEdge IsTerminal IsAwait edge targetIncomings)) &&
                            edge.Payload.Property = IsTerminal

        // Special case: between two nodes are a immediate and a abort transition.
        // Both are deleted, if target or source is simple and has only two outgoing/incoming transitions respecitvely.
        // Simplicity is checked after this condition is met.
        // This can only be checked after specialCase1 was checked !!
        let specialCase3 = sourceOutgoings.Length >= 2 && targetIncomings.Length >= 2 &&
                            multSpecifiedAndSingleOtherEdge IsImmediate IsAbort edge sourceOutgoings &&
                            multSpecifiedAndSingleOtherEdge IsImmediate IsAbort edge targetIncomings

        if(specialCase1) then
            if isSimpleOrConnector source && onlyImmediatesTerminalOrConditionalSourceFocus then
                handleSourceDeletion finalNodeInfo source target graph
            else if isSimpleOrConnector target && onlyImmediatesTerminalOrConditionalTargetFocus then
                handleTargetDeletion finalNodeInfo source target graph
            else
                callSubsequentAndFilterAlreadyVisitedTargets finalNodeInfo (Seq.toList target.Outgoing) graph
        elif(specialCase2) then
            graph.RemoveEdge edge
            callSubsequentAndFilterAlreadyVisitedTargets finalNodeInfo (Seq.toList target.Outgoing) graph
        elif(specialCase3) then
            if isSimpleOrConnector source && (Seq.toList source.Outgoing).Length = 2 then
                handleSourceDeletion finalNodeInfo source target graph
            else if isSimpleOrConnector target && (Seq.toList target.Incoming).Length = 2 then
                handleTargetDeletion finalNodeInfo source target graph
            else
                callSubsequentAndFilterAlreadyVisitedTargets finalNodeInfo (Seq.toList target.Outgoing) graph
        // Clear cases where the edge is not deleted.
        elif(sourceOutgoings.Length > 1 && targetIncomings.Length > 1 ||
             matchNodes source target ||
             edge.Payload.Property <> IsImmediate && edge.Payload.Property <> IsTerminal && edge.Payload.Property <> IsConditional && edge.Payload.Property <> IsConditionalTerminal ||
             edge.Payload.Property = IsTerminal && not (edge.Payload.Label.Equals "") ||
             edge.Payload.Property = IsConditional && not (edge.Payload.Label.Equals "") ||
             edge.Payload.Property = IsConditionalTerminal && not (edge.Payload.Label.Equals "") ||
             edge.Payload.Property = IsImmediate && not (edge.Payload.Label.Equals "")) then
                callSubsequentAndFilterAlreadyVisitedTargets finalNodeInfo (Seq.toList target.Outgoing) graph
        else 
            // Can a) source or b) target be deleted (no label, no complexity)? If so, delete possible node. If not, immediate transition is not deleted.
            // If source is deleted, change the target of incoming nodes of the source to target. If deleted source is init state, change target to initial state. 
            // If target is deleted, change the source of outgoing nodes of the target to the source. If deleted source is final state, change source to final state.
            // If a final or initial state is removed, that status needs to be reassigned.
            // Target can not be deleted if it has multiple incomings, source can not be deleted if it has multiple outgoings.
            // Target can not be deleted, if current edge is a conditional (else case edge) and target is a simple state with an outgoing aawait transition or a complex state.
            if isSimpleOrConnector source && (Seq.toList source.Outgoing).Length = 1 then
                handleSourceDeletion finalNodeInfo source target graph
            else if isSimpleOrConnector target && (Seq.toList target.Incoming).Length = 1 && 
                     not ((edge.Payload.Property = IsConditional || edge.Payload.Property = IsConditionalTerminal) 
                            && (isActivityCallOrOtherComplex target || checkEdgesForAwait (Seq.toList target.Outgoing))) then
                handleTargetDeletion finalNodeInfo source target graph
            else if (Seq.toList target.Outgoing).Length > 0 then
                callSubsequentAndFilterAlreadyVisitedTargets finalNodeInfo (Seq.toList target.Outgoing) graph
            else
                graph

    /// Updates the status of the target and reassigns source's incoming and deletes the source node (and not updated edges).
    and private handleSourceDeletion (finalNodeInfo : (string * bool) list) (source : BlechNode) (target : BlechNode) (graph : VisGraph) : VisGraph =  
        let statusChangedTarget = updateStatusOfNodeDependingOfSuccessorOrPredecessor finalNodeInfo target source graph
        let labelChangedTarget = match source.Payload.Label with 
                                    | "" -> statusChangedTarget
                                    | _ -> graph.ReplacePayloadInByAndReturn statusChangedTarget (addPrefixToNodeLabel source.Payload.Label statusChangedTarget) 
        let updatedTarget = updateEdgesCollapseImmediate finalNodeInfo (Seq.toList source.Incoming) labelChangedTarget Target graph
        graph.RemoveNode source
        callSubsequentAndFilterAlreadyVisitedTargets finalNodeInfo (Seq.toList updatedTarget.Outgoing) graph

    /// Updates the status of the source and reassigns source's incoming and deletes the target node (and not updated edges).
    and private handleTargetDeletion (finalNodeInfo : (string * bool) list) (source : BlechNode) (target : BlechNode) (graph : VisGraph) : VisGraph =  
        let statusChangedSource = updateStatusOfNodeDependingOfSuccessorOrPredecessor finalNodeInfo source target graph
        let labelChangedSource = match target.Payload.Label with 
                                    | "" -> statusChangedSource
                                    | _ -> graph.ReplacePayloadInByAndReturn statusChangedSource (addPostdixToNodeLabel target.Payload.Label statusChangedSource)
        let updatedSource = updateEdgesCollapseImmediate finalNodeInfo (Seq.toList target.Outgoing) labelChangedSource Source graph
        graph.RemoveNode target
        callSubsequentAndFilterAlreadyVisitedTargets finalNodeInfo (Seq.toList updatedSource.Outgoing) graph

    /// Adds a list of new edges to the graph.
    /// New edges are based on the data given by the edges, the information whether source or target is to be changed and the given node to be the new source/target.
    /// If the edge is immediate and the new source is a complex node (everytime it is not simple), change the edge to a termination edge.
    /// If source is a connector and we connect an await edge to it, make it a simple state instead.
    /// If we are connecting an await node to a complex state with a final node, change its property to a terminating await statement.
    /// NOTE that await conditions
    and private updateEdgesCollapseImmediate (finalNodeInfo : (string * bool) list) (edgeList : BlechEdge list) (newTargetOrSource : BlechNode) (sourceOrTarget : SourceOrTarget) (graph : VisGraph) : BlechNode = 
        match edgeList with 
            | head :: tail  ->  let updatedSourceOrTarget = 
                                    match sourceOrTarget with
                                        | Source -> 
                                                let newSource = if newTargetOrSource.Payload.IsComplex = IsConnector && head.Payload.Property = IsAwait then  
                                                                    graph.ReplacePayloadInByAndReturn newTargetOrSource (newTargetOrSource.Payload.SetComplex IsSimple)
                                                                else 
                                                                    newTargetOrSource
                                                // Due to being a new "node", transitions in the edge list might point to inacurrate nodes (self-loops especially).
                                                // Get real nodes from graph.
                                                let target = findNodeByStateCount head.Target.Payload.StateCount head.Target.Payload.SecondaryId graph
                                                if not (isSimpleOrConnector newSource) && (head.Payload.Property = IsImmediate || head.Payload.Property = IsConditional) then
                                                    if (head.Payload.Property = IsImmediate) then 
                                                        graph.AddEdge (head.Payload.CopyAsNotSimplified.CopyWithProperty IsTerminal) newSource target 
                                                    else 
                                                        graph.AddEdge (head.Payload.CopyAsNotSimplified.CopyWithProperty IsConditionalTerminal) newSource target
                                                elif nodeIsCmplxAndHasFinalNode finalNodeInfo newSource && head.Payload.Property = IsAwait then 
                                                    graph.AddEdge (head.Payload.CopyAsNotSimplified.CopyWithProperty IsTerminalAwait) newSource target
                                                else
                                                    graph.AddEdge head.Payload.CopyAsNotSimplified newSource target
                                                newSource
                                        | Target -> graph.AddEdge head.Payload.CopyAsNotSimplified head.Source newTargetOrSource
                                                    newTargetOrSource
                                updateEdgesCollapseImmediate finalNodeInfo tail updatedSourceOrTarget sourceOrTarget graph
            | [] -> newTargetOrSource

    /// Calls the immediate collapse on every graph of a cobegin body.
    and private immediateCollapseCallOnCobegin (finalNodeInfo : (string * bool) list) (regions : (VisGraph * Strength) list) (graph : VisGraph) : VisGraph=
        match regions with 
            | (innerGraph, _) :: tail -> collapseTransient finalNodeInfo innerGraph |> immediateCollapseCallOnCobegin finalNodeInfo tail
            | [] -> graph