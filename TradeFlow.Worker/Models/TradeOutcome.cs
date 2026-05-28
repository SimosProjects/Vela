namespace TradeFlow.Worker.Models;

public enum TradeOutcome
{
    Open,         // still running
    TargetHit,    // profit target reached
    StoppedOut,   // stop loss hit
    XtradesExit,  // closed by Xtrades stc signal
    Averaged,     // averaged into position
    Cancelled,    // order cancelled
    ForcedClose,  // order was forced to clase
    Expired       // options contract expired worthless
}