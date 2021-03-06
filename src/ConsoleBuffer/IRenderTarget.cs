﻿namespace ConsoleBuffer
{
    public interface IRenderTarget
    {
        /// <summary>
        /// Instructs the target to render the character 'c' at x/y coordinates.
        /// </summary>
        /// <param name="x">Horizontal offset.</param>
        /// <param name="y">Vertical offset.</param>
        /// <param name="c">Character to render.</param>
        void RenderCharacter(Character c, int x, int y);
    }
}
