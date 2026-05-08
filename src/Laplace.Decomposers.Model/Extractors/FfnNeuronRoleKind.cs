namespace Laplace.Decomposers.Model.Extractors;

/// <summary>
/// Discriminator for the two per-neuron FFN matrix roles under the Geva 2021
/// key-value framing: each FFN neuron N has a W_up row that defines its
/// "key" (which inputs activate it) and a W_down column that defines its
/// "value" (what output distribution it writes when activated). Both project
/// to POINT4D operator shapes in the model_weights_4d physicality partition.
/// </summary>
public enum FfnNeuronRoleKind
{
    /// <summary>W_up row — input → neuron key pattern.</summary>
    UpKey,
    /// <summary>W_down column — neuron → output value distribution.</summary>
    DownValue,
}
