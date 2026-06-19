/*
 * License: Apache-2.0
 * Copyright 2026 Stefan Kalysta (stefan@redninjas.dev)
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Godot;

namespace Vantix.Smoke;

/// <summary>Pure-logic grenade charge: longer hold = stronger throw (0..1). Deterministic and
/// Godot-independent, so the server can replay it and match the client.</summary>
public class GrenadeController
{
	public SvConVars Sv = ConVars.Sv;

	public float Charge { get; private set; }
	public bool DidThrowThisFrame { get; private set; }
	public float ThrownCharge { get; private set; }

	private bool _wasHeld;

	/// <summary>Server-replayable step. Detects the release edge and triggers the throw.</summary>
	public void Step(GrenadeInput input)
	{
		DidThrowThisFrame = false;

		if (!input.SlotActive)
		{
			Charge = 0f;
			_wasHeld = false;
			return;
		}

		if (input.ThrowHeld)
		{
			Charge = Mathf.Min(1f, Charge + input.Dt / Mathf.Max(0.01f, Sv.GrenadeChargeToFull));
		}
		else if (_wasHeld)
		{
			DidThrowThisFrame = true;
			ThrownCharge = Mathf.Max(Sv.GrenadeMinCharge, Charge);
			Charge = 0f;
		}

		_wasHeld = input.ThrowHeld;
	}
}
